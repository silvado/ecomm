import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { problemTitle } from '../../core/auth';
import { CHANNEL_LABELS, IntegrationLogFilter, IntegrationLogPage, IntegrationsApi, MessageFailure } from './integrations.api';
import { SyncGuardianSection } from './sync-guardian';

const WHEN = new Intl.DateTimeFormat('pt-BR', { timeZone: 'America/Sao_Paulo', dateStyle: 'short', timeStyle: 'medium' });

/**
 * Integrações da loja (RF28/RF29): falhas de processamento em segundo plano (com reprocessar) e o log de todas as
 * chamadas a serviços externos — pagamento, frete e, depois, canais de venda.
 */
@Component({
  selector: 'app-integrations',
  imports: [FormsModule, RouterLink, SyncGuardianSection],
  template: `
    <main class="card wide">
      <h1>Integrações</h1>
      <p class="muted">
        Chamadas da loja a serviços externos (gateway de pagamento, frete). Quando um serviço fica fora do ar, o sistema tenta de novo
        sozinho por cerca de 40 minutos; se não der certo, a falha aparece aqui para você mandar reprocessar depois que o serviço voltar.
      </p>

      <app-sync-guardian />

      <h2>Falhas pendentes</h2>
      @if (failuresError()) {
        <p class="error" role="alert">{{ failuresError() }}</p>
      }
      @if (failures().length) {
        <table data-failures>
          <thead>
            <tr><th>Quando</th><th>O que estava sendo feito</th><th>Motivo</th><th class="num">Tentativas</th><th><span class="sr-only">Ações</span></th></tr>
          </thead>
          <tbody>
            @for (f of failures(); track f.id) {
              <tr>
                <td>{{ when(f.failedAt) }}</td>
                <td>{{ f.description }}</td>
                <td>{{ f.errorSummary ?? f.errorType }}</td>
                <td class="num">{{ f.attempts }}</td>
                <td class="actions">
                  <button type="button" class="link" (click)="replay(f)" [disabled]="busy()" [attr.data-replay]="f.id">Reprocessar</button>
                </td>
              </tr>
            }
          </tbody>
        </table>
      } @else {
        <p class="ok">Nenhuma falha pendente.</p>
      }

      <h2>Log de chamadas</h2>
      <div class="filters">
        <select [(ngModel)]="filter.channel" (ngModelChange)="load(1)" aria-label="Serviço">
          <option value="">Todos os serviços</option>
          @for (c of channels; track c) {
            <option [value]="c">{{ channelLabels[c] }}</option>
          }
        </select>
        <select [(ngModel)]="filter.status" (ngModelChange)="load(1)" aria-label="Situação">
          <option value="">Todas</option>
          <option value="ok">Deram certo</option>
          <option value="falha">Falharam</option>
        </select>
        <label>De <input type="date" [(ngModel)]="filter.from" (ngModelChange)="load(1)" /></label>
        <label>Até <input type="date" [(ngModel)]="filter.to" (ngModelChange)="load(1)" /></label>
      </div>
      @if (logError()) {
        <p class="error" role="alert">{{ logError() }}</p>
      }
      <table data-log>
        <thead>
          <tr><th>Quando</th><th>Serviço</th><th>Operação</th><th>Situação</th><th>Pedido</th><th>Detalhe</th><th class="num">Duração</th></tr>
        </thead>
        <tbody>
          @for (l of page()?.items ?? []; track l.id) {
            <tr>
              <td>{{ when(l.at) }}</td>
              <td>{{ channelLabels[l.channel] ?? l.channel }}</td>
              <td>{{ l.operation }}</td>
              <td><span class="badge" [class.ok]="l.success" [class.warn]="!l.success">{{ l.success ? 'OK' : 'Falhou' }}</span></td>
              <td>
                @if (l.orderNumber) {
                  <a [routerLink]="['/pedidos']" [queryParams]="{ busca: l.orderNumber }">nº {{ l.orderNumber }}</a>
                }
              </td>
              <td class="muted">{{ l.errorSummary ?? l.reference ?? '' }}</td>
              <td class="num">{{ l.durationMs }} ms</td>
            </tr>
          } @empty {
            <tr><td colspan="7" class="muted">Nenhuma chamada registrada (o log guarda 90 dias).</td></tr>
          }
        </tbody>
      </table>
      @if (page(); as p) {
        @if (p.total > p.pageSize) {
          <div class="pager">
            <button type="button" class="link" [disabled]="p.page <= 1" (click)="load(p.page - 1)">Anterior</button>
            <span class="muted">Página {{ p.page }} · {{ p.total }} chamadas</span>
            <button type="button" class="link" [disabled]="p.page * p.pageSize >= p.total" (click)="load(p.page + 1)">Próxima</button>
          </div>
        }
      }
    </main>
  `,
})
export class IntegrationsPage implements OnInit {
  private readonly api = inject(IntegrationsApi);

  protected readonly channels = Object.keys(CHANNEL_LABELS);
  protected readonly channelLabels = CHANNEL_LABELS;
  protected filter: IntegrationLogFilter = { channel: '', status: '', from: '', to: '' };
  protected readonly failures = signal<MessageFailure[]>([]);
  protected readonly page = signal<IntegrationLogPage | null>(null);
  protected readonly busy = signal(false);
  protected readonly failuresError = signal<string | null>(null);
  protected readonly logError = signal<string | null>(null);
  protected readonly when = (iso: string) => WHEN.format(new Date(iso));

  async ngOnInit(): Promise<void> {
    await Promise.all([this.loadFailures(), this.load(1)]);
  }

  protected async replay(f: MessageFailure): Promise<void> {
    this.busy.set(true);
    this.failuresError.set(null);
    try {
      await this.api.replay(f.id);
      await this.loadFailures();
    } catch (error) {
      this.failuresError.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }

  protected async load(pageNumber: number): Promise<void> {
    this.logError.set(null);
    try {
      this.page.set(await this.api.log({ ...this.filter, page: pageNumber }));
    } catch (error) {
      this.logError.set(problemTitle(error));
    }
  }

  private async loadFailures(): Promise<void> {
    try {
      this.failures.set(await this.api.failures(true));
    } catch (error) {
      this.failuresError.set(problemTitle(error));
    }
  }
}
