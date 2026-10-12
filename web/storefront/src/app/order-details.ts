import { CurrencyPipe, DOCUMENT } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, input, output, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { CheckoutApi, OrderStatus, StoreOrder } from './checkout';

const WHEN = new Intl.DateTimeFormat('pt-BR', { timeZone: 'America/Sao_Paulo', dateStyle: 'short', timeStyle: 'short' });

export const formatWhen = (iso: string) => WHEN.format(new Date(iso));

interface OrderStep {
  label: string;
  done: boolean;
  current: boolean;
  at: string | null;
}

/**
 * Pedido como o comprador o vê: etapas, aviso da situação, itens, totais e entrega. Usado pelo link do pedido
 * (/pedido#token, com o botão de pagar) e pela área do cliente (/meus-pedidos, sem o token do pedido).
 */
@Component({
  selector: 'app-order-details',
  imports: [CurrencyPipe],
  template: `
    @let o = order();
    <h1>Pedido nº {{ o.number }}</h1>
    @if (steps(o); as s) {
      <ol class="order-steps" aria-label="Andamento do pedido">
        @for (step of s; track step.label) {
          <li [class.done]="step.done" [attr.aria-current]="step.current ? 'step' : null">
            <span>{{ step.label }}</span>
            @if (step.at) {
              <small>{{ when(step.at) }}</small>
            }
          </li>
        }
      </ol>
    }
    @switch (o.status) {
      @case ('pendingPayment') {
        <p class="notice">Pedido recebido! As peças ficam reservadas para você até <strong>{{ when(o.paymentDeadline) }}</strong>, aguardando o pagamento.</p>
        @if (token()) {
          <div class="totals">
            <button type="button" (click)="pay()" [disabled]="paying()">{{ paying() ? 'Abrindo o pagamento…' : 'Pagar agora' }}</button>
          </div>
          @if (payError()) {
            <p class="problem" role="alert">{{ payError() }}</p>
          }
        } @else {
          <p class="muted">Para pagar, use o botão do e-mail "Pedido recebido" que a loja enviou para você.</p>
        }
        <p class="muted">Já pagou? A confirmação pode levar alguns instantes. <button type="button" class="link" (click)="reload.emit()">Atualizar</button></p>
      }
      @case ('paid') {
        <p class="notice">
          Pagamento confirmado. Obrigado pela compra!
          {{ o.deliveryMethod === 'pickup' ? 'As peças já podem ser retiradas na loja.' : 'Avisaremos aqui quando o pedido for enviado.' }}
        </p>
      }
      @case ('shipped') {
        <p class="notice">
          Pedido enviado em {{ when(o.shippedAt!) }}.
          @if (o.trackingCode) {
            Código de rastreio: <strong class="tracking">{{ o.trackingCode }}</strong> ({{ o.carrier }}).
          }
        </p>
      }
      @case ('delivered') {
        <p class="notice">Pedido entregue em {{ when(o.deliveredAt!) }}. Obrigado pela compra!</p>
      }
      @case ('returned') {
        <p class="notice">Pedido devolvido. Em caso de dúvida sobre o reembolso, fale com a loja.</p>
      }
      @case ('pickedUp') {
        <p class="notice">Peças retiradas na loja em {{ when(o.deliveredAt!) }}. Obrigado pela compra!</p>
      }
      @case ('needsReview') {
        <p class="notice">Pagamento recebido e em análise pela loja. Ela vai entrar em contato com você.</p>
      }
      @case ('canceled') {
        <p class="notice">Pedido cancelado. {{ o.cancelReason }}</p>
      }
    }
    @if (token()) {
      <p class="muted">Guarde o endereço desta página: é por ele que você acompanha o pedido. Feito em {{ when(o.placedAt) }}.</p>
    } @else {
      <p class="muted">Feito em {{ when(o.placedAt) }}.</p>
    }

    <ul class="cart-lines order-lines">
      @for (item of o.items; track item.partId) {
        <li>
          <span>{{ item.quantity }} × {{ item.title }}</span>
          <strong>{{ item.unitPrice * item.quantity | currency: 'BRL' : 'symbol' : '1.2-2' }}</strong>
        </li>
      }
    </ul>

    <section class="totals">
      <p><span>Peças</span> <strong>{{ o.itemsTotal | currency: 'BRL' : 'symbol' : '1.2-2' }}</strong></p>
      <p><span>Frete</span> <strong>{{ o.shippingTotal | currency: 'BRL' : 'symbol' : '1.2-2' }}</strong></p>
      <p class="total"><span>Total</span> <strong>{{ o.total | currency: 'BRL' : 'symbol' : '1.2-2' }}</strong></p>
    </section>

    <section>
      <h2>{{ o.deliveryMethod === 'pickup' ? 'Retirada na loja' : 'Entrega' }}</h2>
      @if (o.deliveryMethod === 'pickup') {
        <p class="pre">{{ o.pickupAddress }}</p>
      } @else if (o.address; as a) {
        <p>
          {{ o.buyerName }}<br />
          {{ a.street }}, {{ a.number }}{{ a.complement ? ' — ' + a.complement : '' }}<br />
          {{ a.district }} · {{ a.city }}/{{ a.state }} · CEP {{ a.postalCode.slice(0, 5) }}-{{ a.postalCode.slice(5) }}
        </p>
        <p class="muted">{{ o.carrier }} · {{ o.service }} — até {{ o.deliveryDays }} dias úteis após o envio.</p>
      }
    </section>
  `,
})
export class OrderDetails {
  private readonly api = inject(CheckoutApi);
  private readonly document = inject(DOCUMENT);

  readonly order = input.required<StoreOrder>();
  /** Token do link do pedido: só com ele dá para pagar daqui. */
  readonly token = input<string | null>(null);
  readonly reload = output<void>();

  protected readonly paying = signal(false);
  protected readonly payError = signal<string | null>(null);
  protected readonly when = formatWhen;

  /** Etapas do pedido para o comprador; cancelado e em análise ficam só com o aviso. */
  protected steps(o: StoreOrder): OrderStep[] | null {
    const order: OrderStatus[] =
      o.deliveryMethod === 'pickup' ? ['pendingPayment', 'paid', 'pickedUp'] : ['pendingPayment', 'paid', 'shipped', 'delivered'];
    const position = order.indexOf(o.status);
    if (position < 0) return null;
    const labels: Partial<Record<OrderStatus, string>> = {
      pendingPayment: 'Pedido feito',
      paid: 'Pago',
      shipped: 'Enviado',
      delivered: 'Entregue',
      pickedUp: 'Retirado',
    };
    const at: Partial<Record<OrderStatus, string | null>> = {
      pendingPayment: o.placedAt,
      shipped: o.shippedAt,
      delivered: o.deliveredAt,
      pickedUp: o.deliveredAt,
    };
    return order.map((status, i) => ({ label: labels[status]!, done: i <= position, current: i === position, at: i <= position ? (at[status] ?? null) : null }));
  }

  protected async pay(): Promise<void> {
    const token = this.token();
    if (!token) return;
    this.paying.set(true);
    this.payError.set(null);
    try {
      const { redirectUrl } = await firstValueFrom(this.api.pay(token));
      this.document.location.assign(redirectUrl);
    } catch (error) {
      this.payError.set(
        error instanceof HttpErrorResponse && typeof error.error?.title === 'string' ? error.error.title : 'Não foi possível abrir o pagamento. Tente de novo.',
      );
      this.paying.set(false);
    }
  }
}
