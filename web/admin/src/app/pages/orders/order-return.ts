import { CurrencyPipe } from '@angular/common';
import { Component, computed, inject, input, OnInit, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { problemTitle } from '../../core/auth';
import { OrdersApi, OrderView, REFUND_METHOD_LABELS, RefundMethod, RETURN_REASON_LABELS, ReturnReason } from './orders.api';

interface Line {
  partId: string;
  internalCode: string;
  title: string;
  unitPrice: number;
  available: number;
  quantity: number;
  restock: boolean;
}

const DAY_MS = 24 * 60 * 60 * 1000;

/**
 * Devolução de peças (RF20): escolhe quanto de cada peça voltou e se volta ao estoque (peça avariada não volta), o
 * motivo e o reembolso que a loja fez — só registrado; o estorno em si é feito no gateway ou no caixa.
 */
@Component({
  selector: 'app-order-return',
  imports: [FormsModule, CurrencyPipe],
  template: `
    <form class="confirm" (ngSubmit)="submit()" data-return>
      <p>
        <strong>Registrar devolução do pedido nº {{ order().number }}</strong>
        @if (days() !== null) {
          · {{ order().status === 'pickedUp' ? 'retirado' : 'entregue' }} há {{ days() }} {{ days() === 1 ? 'dia' : 'dias' }}
          <small class="muted">(arrependimento: 7 dias para compra fora da loja; garantia de usada: conforme a política da loja)</small>
        }
      </p>
      <table>
        <thead>
          <tr><th>Peça</th><th class="num">Devolver</th><th>Volta ao estoque?</th><th class="num">Valor</th></tr>
        </thead>
        <tbody>
          @for (l of lines(); track l.partId; let i = $index) {
            <tr>
              <td>{{ l.internalCode }} · {{ l.title }} <small class="muted">(até {{ l.available }})</small></td>
              <td class="num">
                <input type="number" min="0" [max]="l.available" step="1" [name]="'qtd' + i" [ngModel]="l.quantity" (ngModelChange)="set(i, { quantity: +$event })"
                  [disabled]="!l.available" [attr.aria-label]="'Quantidade devolvida de ' + l.internalCode" />
              </td>
              <td>
                <label class="checkbox">
                  <input type="checkbox" [name]="'estoque' + i" [ngModel]="l.restock" (ngModelChange)="set(i, { restock: $event })" [disabled]="!l.quantity" />
                  {{ l.restock ? 'Sim' : 'Não (avariada)' }}
                </label>
              </td>
              <td class="num">{{ l.quantity * l.unitPrice | currency: 'BRL' : 'symbol' : '1.2-2' : 'pt-BR' }}</td>
            </tr>
          }
        </tbody>
      </table>
      <div class="grid-3">
        <label>
          Motivo
          <select name="reason" [(ngModel)]="reason" required>
            <option value="" disabled>Escolha</option>
            @for (r of reasons; track r) {
              <option [value]="r">{{ reasonLabels[r] }}</option>
            }
          </select>
        </label>
        <label>
          Reembolso ao cliente (R$)
          <input type="number" name="refund" min="0" step="0.01" [(ngModel)]="refund" [placeholder]="suggested().toFixed(2)" />
          <small>Deixe em branco se não houve reembolso. Só fica registrado: estorne no gateway ou no caixa.</small>
        </label>
        <label>
          Forma do reembolso
          <select name="method" [(ngModel)]="method" [disabled]="refund == null">
            <option value="">—</option>
            @for (m of methods; track m) {
              <option [value]="m">{{ methodLabels[m] }}</option>
            }
          </select>
        </label>
      </div>
      <label>Observação <input name="note" maxlength="300" [(ngModel)]="note" /></label>
      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
      <div class="order-actions">
        <button type="submit" [disabled]="busy() || !total() || !reason">Registrar devolução</button>
        <button type="button" class="link" (click)="closed.emit()" [disabled]="busy()">Voltar</button>
      </div>
    </form>
  `,
})
export class OrderReturnForm implements OnInit {
  readonly order = input.required<OrderView>();
  readonly done = output<OrderView>();
  readonly closed = output<void>();

  private readonly api = inject(OrdersApi);

  protected readonly reasons: ReturnReason[] = ['defect', 'wrongPart', 'regret', 'other'];
  protected readonly methods: RefundMethod[] = ['gateway', 'pix', 'cash', 'storeCredit', 'other'];
  protected readonly reasonLabels = RETURN_REASON_LABELS;
  protected readonly methodLabels = REFUND_METHOD_LABELS;
  protected reason: ReturnReason | '' = '';
  protected refund: number | null = null;
  protected method: RefundMethod | '' = '';
  protected note = '';
  protected readonly lines = signal<Line[]>([]);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly total = computed(() => this.lines().reduce((sum, l) => sum + l.quantity, 0));
  protected readonly suggested = computed(() => this.lines().reduce((sum, l) => sum + l.quantity * l.unitPrice, 0));
  protected readonly days = computed(() => {
    const at = this.order().deliveredAt;
    return at ? Math.max(0, Math.floor((Date.now() - new Date(at).getTime()) / DAY_MS)) : null;
  });

  ngOnInit(): void {
    this.lines.set(
      this.order().items.map((i) => ({
        partId: i.partId,
        internalCode: i.internalCode,
        title: i.title,
        unitPrice: i.unitPrice,
        available: i.quantity - i.returnedQuantity,
        quantity: 0,
        restock: true,
      })),
    );
  }

  protected set(index: number, change: Partial<Pick<Line, 'quantity' | 'restock'>>): void {
    this.lines.update((lines) => lines.map((l, i) => (i === index ? { ...l, ...change } : l)));
  }

  protected async submit(): Promise<void> {
    if (!this.total() || !this.reason) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const refund = this.refund == null || Number.isNaN(Number(this.refund)) ? null : Number(this.refund);
      const updated = await this.api.returnItems(this.order().id, {
        items: this.lines().filter((l) => l.quantity > 0).map((l) => ({ partId: l.partId, quantity: l.quantity, restock: l.restock })),
        reason: this.reason,
        note: this.note.trim() || null,
        refundAmount: refund,
        refundMethod: refund == null ? null : this.method || null,
      });
      this.done.emit(updated);
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }
}
