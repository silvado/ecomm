import { Component, inject, input, OnInit, signal } from '@angular/core';
import { problemTitle } from '../../core/auth';
import { CHANNEL_LABELS, IntegrationLogEntry, IntegrationsApi } from './integrations.api';

const WHEN = new Intl.DateTimeFormat('pt-BR', { timeZone: 'America/Sao_Paulo', dateStyle: 'short', timeStyle: 'medium' });

/** Chamadas externas de um pedido ou de uma peça (RF29 CA2), mais recentes primeiro. */
@Component({
  selector: 'app-integration-log',
  template: `
    <h3>Integrações</h3>
    @if (error()) {
      <p class="error" role="alert">{{ error() }}</p>
    } @else if (items().length) {
      <table data-integration-log>
        <tbody>
          @for (l of items(); track l.id) {
            <tr>
              <td>{{ when(l.at) }}</td>
              <td>{{ channel(l.channel) }} · {{ l.operation }}</td>
              <td><span class="badge" [class.ok]="l.success" [class.warn]="!l.success">{{ l.success ? 'OK' : 'Falhou' }}</span></td>
              <td class="muted">{{ l.errorSummary ?? l.reference ?? '' }} · {{ l.durationMs }} ms</td>
            </tr>
          }
        </tbody>
      </table>
    } @else {
      <p class="muted">Nenhuma chamada a serviços externos registrada.</p>
    }
  `,
})
export class IntegrationLog implements OnInit {
  readonly orderId = input<string>();
  readonly partId = input<string>();

  private readonly api = inject(IntegrationsApi);
  protected readonly items = signal<IntegrationLogEntry[]>([]);
  protected readonly error = signal<string | null>(null);
  protected readonly when = (iso: string) => WHEN.format(new Date(iso));
  protected readonly channel = (c: string) => CHANNEL_LABELS[c] ?? c;

  async ngOnInit(): Promise<void> {
    try {
      this.items.set((await this.api.log({ orderId: this.orderId(), partId: this.partId() }, 20)).items);
    } catch (error) {
      this.error.set(problemTitle(error));
    }
  }
}
