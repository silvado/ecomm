import { CurrencyPipe, DatePipe, DOCUMENT } from '@angular/common';
import { Component, inject, OnInit, signal } from '@angular/core';
import { problemTitle } from '../../core/auth';
import { invoiceStatusLabel, SubscriptionInvoice } from '../../shared/invoice';
import { SubscriptionApi, SubscriptionView } from './subscription.api';

export type { SubscriptionView } from './subscription.api';

/**
 * Assinatura da loja com a plataforma (RF38), só para o Dono: plano, mensalidade e faturas. Pagar leva à página do
 * gateway da plataforma; ao voltar, a fatura aparece paga assim que o gateway confirma.
 */
@Component({
  selector: 'app-subscription',
  imports: [CurrencyPipe, DatePipe],
  template: `
    <main class="card wide">
      <h1>Assinatura</h1>
      @if (view(); as v) {
        <p data-plan>
          Plano <strong>{{ v.planName }}</strong> · mensalidade {{ v.monthlyPrice | currency: 'BRL' : 'symbol' : '1.2-2' : 'pt-BR' }}
        </p>
        @if (v.startedOn) {
          <p class="muted">Cobrança desde {{ v.startedOn | date: 'dd/MM/yyyy' : 'UTC' }}; a mensalidade vence todo dia {{ v.dueDay }}.</p>
        } @else {
          <p class="muted">A mensalidade começa quando a loja for publicada (Primeiros passos).</p>
        }
        @if (!v.onlinePayment) {
          <p class="muted" data-offline>O pagamento pelo painel ainda não está disponível: fale com a equipe da plataforma para pagar por Pix ou transferência.</p>
        }

        @if (v.invoices.length) {
          <table>
            <thead><tr><th>Fatura</th><th>Vencimento</th><th class="num">Valor</th><th>Situação</th><th></th></tr></thead>
            <tbody>
              @for (i of v.invoices; track i.id) {
                <tr [attr.data-invoice]="i.id">
                  <td>{{ i.description }}</td>
                  <td>{{ i.dueDate | date: 'dd/MM/yyyy' : 'UTC' }}</td>
                  <td class="num">{{ i.amount | currency: 'BRL' : 'symbol' : '1.2-2' : 'pt-BR' }}</td>
                  <td><span class="badge" [class.danger]="i.overdue">{{ statusLabel(i) }}</span></td>
                  <td>
                    @if (i.status === 'open' && v.onlinePayment) {
                      <button type="button" (click)="pay(i)" [disabled]="busy()" data-pay>Pagar</button>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        } @else {
          <p class="muted">Nenhuma fatura até agora.</p>
        }
      }
      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
    </main>
  `,
})
export class SubscriptionPage implements OnInit {
  private readonly api = inject(SubscriptionApi);
  private readonly document = inject(DOCUMENT);

  protected readonly view = signal<SubscriptionView | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly statusLabel = invoiceStatusLabel;

  async ngOnInit(): Promise<void> {
    try {
      this.view.set(await this.api.get());
    } catch (error) {
      this.error.set(problemTitle(error));
    }
  }

  protected async pay(invoice: SubscriptionInvoice): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      const { redirectUrl } = await this.api.pay(invoice.id);
      this.document.location.assign(redirectUrl);
    } catch (error) {
      this.error.set(problemTitle(error));
      this.busy.set(false);
    }
  }
}
