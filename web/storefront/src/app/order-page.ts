import { CurrencyPipe, DOCUMENT } from '@angular/common';
import { afterNextRender, Component, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { HttpErrorResponse } from '@angular/common/http';
import { CheckoutApi, lastOrderToken, OrderStatus, StoreOrder } from './checkout';
import { Seo } from './seo';
import { StoreContext } from './store';

const WHEN = new Intl.DateTimeFormat('pt-BR', { timeZone: 'America/Sao_Paulo', dateStyle: 'short', timeStyle: 'short' });

interface OrderStep {
  label: string;
  done: boolean;
  current: boolean;
  at: string | null;
}

/**
 * Acompanhamento do pedido pelo link do comprador (/pedido#token). O token fica no fragmento da URL, que o navegador
 * nunca envia ao servidor: a página sai do SSR em "Carregando…" e busca o pedido daqui.
 */
@Component({
  selector: 'app-order',
  imports: [CurrencyPipe, RouterLink],
  template: `
    @switch (state()) {
      @case ('loading') {
        <p class="muted">Carregando…</p>
      }
      @case ('notFound') {
        <h1>Pedido não encontrado</h1>
        <p>Confira se o endereço desta página está completo. <a routerLink="/">Voltar à loja</a></p>
      }
      @default {
        @if (order(); as o) {
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
              <div class="totals">
                <button type="button" (click)="pay()" [disabled]="paying()">{{ paying() ? 'Abrindo o pagamento…' : 'Pagar agora' }}</button>
              </div>
              @if (payError()) {
                <p class="problem" role="alert">{{ payError() }}</p>
              }
              <p class="muted">Já pagou? A confirmação pode levar alguns instantes. <button type="button" class="link" (click)="reload()">Atualizar</button></p>
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
          <p class="muted">Guarde o endereço desta página: é por ele que você acompanha o pedido. Feito em {{ when(o.placedAt) }}.</p>

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
        }
      }
    }
  `,
})
export class OrderPage {
  private readonly api = inject(CheckoutApi);
  private readonly route = inject(ActivatedRoute);
  private readonly document = inject(DOCUMENT);
  private token: string | null = null;

  protected readonly paying = signal(false);
  protected readonly payError = signal<string | null>(null);

  protected readonly state = signal<'loading' | 'notFound' | 'ok'>('loading');
  protected readonly order = signal<StoreOrder | null>(null);
  protected readonly when = (iso: string) => WHEN.format(new Date(iso));

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

  constructor() {
    const ctx = inject(StoreContext);
    inject(Seo).apply({ title: `Pedido — ${ctx.store?.name ?? 'Loja'}`, description: 'Acompanhamento do pedido.', path: '/pedido', origin: ctx.origin, noindex: true });
    afterNextRender(() => void this.load());
  }

  protected async pay(): Promise<void> {
    if (!this.token) return;
    this.paying.set(true);
    this.payError.set(null);
    try {
      const { redirectUrl } = await firstValueFrom(this.api.pay(this.token));
      this.document.location.assign(redirectUrl);
    } catch (error) {
      this.payError.set(
        error instanceof HttpErrorResponse && typeof error.error?.title === 'string' ? error.error.title : 'Não foi possível abrir o pagamento. Tente de novo.',
      );
      this.paying.set(false);
    }
  }

  protected reload(): Promise<void> {
    return this.load();
  }

  private async load(): Promise<void> {
    // Volta do gateway chega sem o token: usa o último pedido feito neste navegador.
    const token = this.route.snapshot.fragment || lastOrderToken();
    this.token = token;
    if (!token) {
      this.state.set('notFound');
      return;
    }
    try {
      this.order.set(await firstValueFrom(this.api.find(token)));
      this.state.set('ok');
    } catch {
      this.state.set('notFound');
    }
  }
}
