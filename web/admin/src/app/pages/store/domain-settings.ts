import { DatePipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { Component, inject, Injectable, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { problemTitle } from '../../core/auth';

export type DomainKind = 'customWww' | 'customApex';
export type DomainStatus = 'pending' | 'verified' | 'failed';

export interface DnsInstruction {
  type: string;
  name: string;
  value: string;
  note: string;
  alternative: boolean;
}

export interface CustomDomain {
  id: string;
  host: string;
  kind: DomainKind;
  status: DomainStatus;
  isPrimary: boolean;
  requestedAt: string | null;
  lastCheckedAt: string | null;
  lastCheckMessage: string | null;
  verifiedAt: string | null;
  records: DnsInstruction[];
}

export interface CustomDomains {
  allowed: boolean;
  platformHost: string;
  domains: CustomDomain[];
}

/** Domínio próprio da loja (RF03): a loja aponta o DNS e a plataforma confere sozinha a cada poucos minutos. */
@Injectable({ providedIn: 'root' })
export class DomainSettingsApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/painel/loja/dominios';

  list(): Promise<CustomDomains> {
    return firstValueFrom(this.http.get<CustomDomains>(this.base));
  }

  add(dominio: string): Promise<CustomDomains> {
    return firstValueFrom(this.http.post<CustomDomains>(this.base, { dominio }));
  }

  remove(id: string): Promise<CustomDomains> {
    return firstValueFrom(this.http.delete<CustomDomains>(`${this.base}/${id}`));
  }

  verify(id: string): Promise<CustomDomains> {
    return firstValueFrom(this.http.post<CustomDomains>(`${this.base}/${id}/verificar`, {}));
  }
}

const STATUS: Record<DomainStatus, string> = {
  pending: 'Aguardando o DNS',
  verified: 'Funcionando',
  failed: 'Não encontramos os registros',
};

/** Em Dados da loja: cadastro do domínio próprio, registros a criar no DNS e situação da verificação. */
@Component({
  selector: 'app-domain-settings',
  imports: [FormsModule, DatePipe],
  template: `
    <section data-domain-settings>
      <h2>Domínio próprio</h2>
      @if (view(); as v) {
        <p class="muted">Sua loja sempre funciona em <strong>{{ v.platformHost }}</strong>.</p>
        @if (!v.allowed && v.domains.length === 0) {
          <p class="notice" role="status" data-domain-plan>
            O domínio próprio (ex.: www.sualoja.com.br) faz parte dos planos Profissional e Completo. Para trocar de plano, veja o
            cartão "Seu plano" no Início.
          </p>
        } @else {
          @for (d of v.domains; track d.id) {
            <article class="domain" [attr.data-domain]="d.host">
              <h3>
                {{ d.host }}
                <span class="badge" [class.ok]="d.status === 'verified'" [class.warn]="d.status !== 'verified'">{{ statusText(d.status) }}</span>
                @if (d.isPrimary) { <span class="badge ok">Endereço principal</span> }
              </h3>
              @if (d.status === 'verified') {
                <p class="muted">
                  Verificado em {{ d.verifiedAt | date: 'dd/MM/yyyy HH:mm' }}. O cadeado (HTTPS) é emitido no primeiro acesso.
                  @if (d.isPrimary) { Quem entrar pelo endereço antigo é levado para este. }
                </p>
              } @else {
                <p class="muted">Crie estes registros no painel de DNS do seu domínio (Registro.br, GoDaddy, Cloudflare…):</p>
                <table class="dns">
                  <thead><tr><th>Tipo</th><th>Nome</th><th>Valor</th><th></th></tr></thead>
                  <tbody>
                    @for (r of d.records; track r.type + r.name + r.value) {
                      <tr [class.muted]="r.alternative">
                        <td>{{ r.alternative ? 'ou ' : '' }}{{ r.type }}</td>
                        <td><code>{{ r.name }}</code></td>
                        <td><code>{{ r.value }}</code></td>
                        <td><small>{{ r.note }}</small></td>
                      </tr>
                    }
                  </tbody>
                </table>
                <p class="muted">
                  Conferimos sozinhos a cada poucos minutos, por até 72 horas. A mudança no DNS pode levar algumas horas para valer.
                  @if (d.lastCheckedAt) { Última conferência: {{ d.lastCheckedAt | date: 'dd/MM/yyyy HH:mm' }}. }
                </p>
                @if (d.lastCheckMessage) {
                  <p [class]="d.status === 'failed' ? 'error' : 'warn'" role="status" data-domain-message>{{ d.lastCheckMessage }}</p>
                }
              }
              <div class="order-actions">
                @if (d.status !== 'verified') {
                  <button type="button" (click)="verify(d)" [disabled]="busy()" data-domain-verify>Verificar agora</button>
                }
                <button type="button" class="link danger" (click)="remove(d)" [disabled]="busy()" data-domain-remove>Remover</button>
              </div>
            </article>
          }
          @if (v.allowed && v.domains.length < 2) {
            <div class="order-actions">
              <input [(ngModel)]="host" name="host" placeholder="www.sualoja.com.br" maxlength="253" aria-label="Domínio" data-domain-input />
              <button type="button" (click)="add()" [disabled]="busy() || !host.trim()" data-domain-add>Adicionar domínio</button>
            </div>
            <p class="muted">
              Use o endereço com "www". Se quiser que o endereço sem "www" também funcione, adicione os dois. Os registros de
              e-mail (MX) do seu domínio não são tocados.
            </p>
          }
        }
      }
      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
    </section>
  `,
})
export class DomainSettingsSection implements OnInit {
  private readonly api = inject(DomainSettingsApi);

  protected host = '';
  protected readonly view = signal<CustomDomains | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    await this.run(() => this.api.list());
  }

  protected statusText(status: DomainStatus): string {
    return STATUS[status];
  }

  protected async add(): Promise<void> {
    if (await this.run(() => this.api.add(this.host.trim()))) this.host = '';
  }

  protected verify(domain: CustomDomain): Promise<boolean> {
    return this.run(() => this.api.verify(domain.id));
  }

  protected async remove(domain: CustomDomain): Promise<void> {
    const warning = domain.isPrimary ? ` A loja volta a abrir só em ${this.view()?.platformHost}.` : '';
    if (!confirm(`Remover ${domain.host}?${warning}`)) return;
    await this.run(() => this.api.remove(domain.id));
  }

  private async run(action: () => Promise<CustomDomains>): Promise<boolean> {
    this.busy.set(true);
    this.error.set(null);
    try {
      this.view.set(await action());
      return true;
    } catch (error) {
      this.error.set(problemTitle(error));
      return false;
    } finally {
      this.busy.set(false);
    }
  }
}
