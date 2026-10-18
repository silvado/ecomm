import { DOCUMENT } from '@angular/common';
import { Component, computed, inject, Injectable, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { problemTitle } from '../../core/auth';
import { ChannelConnection, ChannelsApi, ChannelsOverview, CONNECTION_STATUS_LABELS, MetaCatalogView } from './channels.api';

/** Sai do painel para outro site (página de autorização do canal). Separado para os testes. */
@Injectable({ providedIn: 'root' })
export class ExternalNavigation {
  private readonly document = inject(DOCUMENT);

  go(url: string): void {
    this.document.location.assign(url);
  }
}

const WHEN = new Intl.DateTimeFormat('pt-BR', { timeZone: 'America/Sao_Paulo', dateStyle: 'short', timeStyle: 'short' });

/**
 * Canais de venda (RF21): o Dono conecta a conta do Mercado Livre (autoriza no site do ML e volta), vê a situação e
 * desconecta. Publicar anúncios e receber vendas chegam nas próximas versões.
 */
@Component({
  selector: 'app-channels',
  imports: [FormsModule],
  template: `
    <main class="card">
      <h1>Canais de venda</h1>
      <p class="muted">
        Conecte as contas onde a loja vende. O estoque é um só: quando uma peça vende num canal, os outros são atualizados.
      </p>

      <section class="channel" data-channel="mercado-livre">
        <h2>Mercado Livre</h2>
        @if (overview(); as o) {
          @if (!o.mercadoLivreAvailable) {
            <p class="notice">O Mercado Livre ainda não está disponível nesta instalação.</p>
          } @else if (!o.mercadoLivreInPlan) {
            <p class="notice">O plano da loja não inclui o Mercado Livre. Veja os planos em Assinatura.</p>
          } @else {
            @for (c of mercadoLivre(); track c.id) {
              <div class="status-row" [attr.data-connection]="c.id">
                <span class="badge" [class.ok]="c.status === 'active'" [class.warn]="c.status === 'reauthRequired'">{{ statusLabels[c.status] }}</span>
                <strong>{{ c.accountName ?? 'Conta ' + c.externalAccountId }}</strong>
                <span class="muted">desde {{ when(c.createdAt) }}</span>
              </div>
              @if (c.status === 'reauthRequired') {
                <p class="warn" role="alert">
                  {{ c.statusReason ?? 'O Mercado Livre pediu uma nova autorização.' }} Enquanto isso, os anúncios desta conta não são atualizados.
                </p>
              }
              <div class="order-actions">
                @if (c.status === 'reauthRequired') {
                  <button type="button" (click)="connect()" [disabled]="busy()">Autorizar de novo</button>
                }
                <button type="button" class="link danger" (click)="disconnect(c)" [disabled]="busy()">Desconectar</button>
              </div>
            } @empty {
              <p>Nenhuma conta conectada.</p>
            }
            @if (!hasLiveConnection()) {
              <button type="button" (click)="connect()" [disabled]="busy()" data-connect>Conectar Mercado Livre</button>
              <small>
                Você vai para o site do Mercado Livre, entra com a <strong>conta principal</strong> da loja (não a de colaborador) e
                autoriza a plataforma. Depois volta para cá.
              </small>
            }
          }
        } @else if (!error()) {
          <p class="muted">Carregando…</p>
        }
        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }
      </section>

      <section class="channel" data-channel="meta">
        <h2>Instagram e Facebook (catálogo)</h2>
        @if (meta(); as m) {
          @if (metaConnection()?.status === 'reauthRequired') {
            <p class="warn" role="alert">{{ metaConnection()!.statusReason }}</p>
          }
          <div class="status-row">
            <span class="badge" [class.ok]="metaConnection()?.status !== 'reauthRequired'">{{ metaConnection()?.status === 'reauthRequired' ? 'Autorizar de novo' : 'Conectado' }}</span>
            <strong>{{ m.catalogName ?? 'Catálogo ' + m.catalogId }}</strong>
          </div>
          <label>
            Endereço do feed (cole em Gerenciador de Comércio → Catálogo → Fontes de dados → Feed agendado)
            <input type="text" [value]="m.feedUrl" readonly data-feed-url />
          </label>
          <small>Guarde este endereço como uma senha: quem tiver o link vê o catálogo. Estoque e preço também são enviados na hora a cada mudança.</small>
          <div class="order-actions">
            <button type="button" class="link danger" (click)="disconnectMeta()" [disabled]="busy()">Desconectar</button>
          </div>
        } @else {
          <p class="muted">
            Informe o id do catálogo (Gerenciador de Comércio) e um token de acesso de usuário do sistema do Gerenciador de Negócios com
            permissão no catálogo.
          </p>
          <div class="grid-3">
            <label>Id do catálogo <input type="text" [(ngModel)]="catalogId" name="meta-catalog" inputmode="numeric" /></label>
            <label>Token de acesso <input type="password" [(ngModel)]="metaToken" name="meta-token" autocomplete="off" /></label>
          </div>
          <div><button type="button" (click)="connectMeta()" [disabled]="busy() || !catalogId || !metaToken" data-connect-meta>Conectar catálogo</button></div>
        }
        @if (metaError()) {
          <p class="error" role="alert">{{ metaError() }}</p>
        }
      </section>

      <section class="channel">
        <h2>OLX</h2>
        <p class="muted">A conexão chega numa próxima versão.</p>
      </section>
    </main>
  `,
})
export class ChannelsPage implements OnInit {
  private readonly api = inject(ChannelsApi);
  private readonly navigation = inject(ExternalNavigation);

  protected readonly statusLabels = CONNECTION_STATUS_LABELS;
  protected readonly overview = signal<ChannelsOverview | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly when = (iso: string) => WHEN.format(new Date(iso));

  /** Desconectadas não aparecem: só a conta ligada (ou pedindo nova autorização). */
  protected readonly mercadoLivre = computed(() =>
    (this.overview()?.connections ?? []).filter((c) => c.channel === 'mercado-livre' && c.status !== 'disabled'),
  );
  protected readonly hasLiveConnection = computed(() => this.mercadoLivre().length > 0);

  // Catálogo da Meta (RF22).
  protected readonly meta = signal<MetaCatalogView | null>(null);
  protected readonly metaError = signal<string | null>(null);
  protected readonly metaConnection = computed(() => (this.overview()?.connections ?? []).find((c) => c.channel === 'meta' && c.status !== 'disabled'));
  protected catalogId = '';
  protected metaToken = '';

  async connectMeta(): Promise<void> {
    this.busy.set(true);
    this.metaError.set(null);
    try {
      this.meta.set(await this.api.connectMeta(this.catalogId.trim(), this.metaToken.trim()));
      this.metaToken = '';
      await this.load();
    } catch (error) {
      this.metaError.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }

  async disconnectMeta(): Promise<void> {
    if (!confirm('Desconectar o catálogo? O endereço do feed deixa de funcionar e o estoque não é mais enviado.')) return;
    this.busy.set(true);
    this.metaError.set(null);
    try {
      await this.api.disconnectMeta();
      this.meta.set(null);
      await this.load();
    } catch (error) {
      this.metaError.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }

  async ngOnInit(): Promise<void> {
    await this.load();
  }

  async connect(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      this.navigation.go(await this.api.startMercadoLivre());
    } catch (error) {
      this.error.set(problemTitle(error));
      this.busy.set(false);
    }
  }

  async disconnect(c: ChannelConnection): Promise<void> {
    const name = c.accountName ?? c.externalAccountId;
    if (!confirm(`Desconectar a conta ${name}? Os anúncios dela param de ser atualizados pelo estoque da loja.`)) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      await this.api.disconnect(c.id);
      await this.load();
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }

  private async load(): Promise<void> {
    try {
      const [overview, meta] = await Promise.all([this.api.overview(), this.api.meta()]);
      this.overview.set(overview);
      this.meta.set(meta);
    } catch (error) {
      this.error.set(problemTitle(error));
    }
  }
}
