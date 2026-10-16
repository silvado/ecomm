import { CurrencyPipe, DatePipe } from '@angular/common';
import { Component, inject, input, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { problemTitle } from '../core/auth';
import { invoiceStatusLabel, SubscriptionInvoice } from '../shared/invoice';
import { PlatformApi, PlatformBilling } from './platform.api';

/**
 * Cobrança da loja pela plataforma (RF38): faturas, taxa de implantação, pagamento recebido fora do gateway (Pix,
 * transferência) e cancelamento. Cada ação pede uma observação e fica na auditoria.
 */
@Component({
  selector: 'app-platform-billing',
  imports: [CurrencyPipe, DatePipe, FormsModule],
  template: `
    @if (billing(); as b) {
      <p class="muted" data-billing-summary>
        Plano {{ b.planName }} · mensalidade {{ b.monthlyPrice | currency: 'BRL' : 'symbol' : '1.2-2' : 'pt-BR' }}
        @if (b.startedOn) {
          · cobrança desde {{ b.startedOn | date: 'dd/MM/yyyy' : 'UTC' }}, vence todo dia {{ b.dueDay }}
        } @else {
          · a mensalidade começa quando o Dono publicar a loja
        }
      </p>
      @if (b.invoices.length) {
        <table>
          <thead><tr><th>Fatura</th><th>Vencimento</th><th class="num">Valor</th><th>Situação</th><th></th></tr></thead>
          <tbody>
            @for (i of b.invoices; track i.id) {
              <tr [attr.data-invoice]="i.id">
                <td>{{ i.description }} @if (i.note) { <br /><small class="muted">{{ i.note }}</small> }</td>
                <td>{{ i.dueDate | date: 'dd/MM/yyyy' : 'UTC' }}</td>
                <td class="num">{{ i.amount | currency: 'BRL' : 'symbol' : '1.2-2' : 'pt-BR' }}</td>
                <td><span class="badge" [class.danger]="i.overdue">{{ statusLabel(i) }}</span></td>
                <td>
                  @if (i.status === 'open') {
                    <button type="button" class="link" (click)="start(i, 'pay')" [disabled]="busy()" data-record-payment>Registrar pagamento</button>
                    <button type="button" class="link" (click)="start(i, 'cancel')" [disabled]="busy()" data-cancel-invoice>Cancelar</button>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      } @else {
        <p class="muted">Nenhuma fatura ainda.</p>
      }

      @if (pending(); as p) {
        <form class="confirm" (ngSubmit)="confirm()">
          <p>{{ p.action === 'pay' ? 'Registrar como paga' : 'Cancelar' }}: {{ p.invoice.description }}</p>
          <label>
            {{ p.action === 'pay' ? 'Como foi pago' : 'Motivo' }}
            <input name="note" [(ngModel)]="note" maxlength="300" required data-note />
          </label>
          <button type="submit" [disabled]="busy() || !note.trim()" data-confirm-billing>Confirmar</button>
          <button type="button" class="link" (click)="pending.set(null)">Voltar</button>
        </form>
      }

      @if (!hasSetupFee(b)) {
        <form class="filters" (ngSubmit)="addSetupFee()">
          <label>Taxa de implantação (R$) <input name="setupAmount" type="number" min="0.01" step="0.01" [(ngModel)]="setupAmount" data-setup-amount /></label>
          <label>Vencimento <input name="setupDue" type="date" [(ngModel)]="setupDue" data-setup-due /></label>
          <button type="submit" [disabled]="busy() || !setupAmount || !setupDue" data-add-setup>Lançar</button>
        </form>
      }
    }
    @if (error()) {
      <p class="error" role="alert">{{ error() }}</p>
    }
  `,
})
export class PlatformBillingSection implements OnInit {
  private readonly api = inject(PlatformApi);

  readonly tenantId = input.required<string>();
  protected readonly billing = signal<PlatformBilling | null>(null);
  protected readonly pending = signal<{ invoice: SubscriptionInvoice; action: 'pay' | 'cancel' } | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly statusLabel = invoiceStatusLabel;
  protected note = '';
  protected setupAmount: number | null = null;
  protected setupDue = '';

  async ngOnInit(): Promise<void> {
    await this.run(() => this.api.billing(this.tenantId()));
  }

  protected hasSetupFee(b: PlatformBilling): boolean {
    return b.invoices.some((i) => i.kind === 'setup' && i.status !== 'canceled');
  }

  protected start(invoice: SubscriptionInvoice, action: 'pay' | 'cancel'): void {
    this.note = '';
    this.pending.set({ invoice, action });
  }

  protected confirm(): Promise<void> {
    const p = this.pending();
    if (!p) return Promise.resolve();
    const id = this.tenantId();
    return this.run(() =>
      p.action === 'pay' ? this.api.recordPayment(id, p.invoice.id, this.note) : this.api.cancelInvoice(id, p.invoice.id, this.note),
    );
  }

  protected addSetupFee(): Promise<void> {
    return this.run(() => this.api.addSetupFee(this.tenantId(), Number(this.setupAmount), this.setupDue));
  }

  private async run(action: () => Promise<PlatformBilling>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      this.billing.set(await action());
      this.pending.set(null);
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }
}
