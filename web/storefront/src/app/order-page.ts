import { CurrencyPipe } from '@angular/common';
import { afterNextRender, Component, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { CheckoutApi, StoreOrder } from './checkout';
import { Seo } from './seo';
import { StoreContext } from './store';

const WHEN = new Intl.DateTimeFormat('pt-BR', { timeZone: 'America/Sao_Paulo', dateStyle: 'short', timeStyle: 'short' });

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
          @switch (o.status) {
            @case ('pendingPayment') {
              <p class="notice">Pedido recebido! As peças ficam reservadas para você até <strong>{{ when(o.paymentDeadline) }}</strong>, aguardando o pagamento.</p>
            }
            @case ('paid') {
              <p class="notice">Pagamento confirmado.</p>
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

  protected readonly state = signal<'loading' | 'notFound' | 'ok'>('loading');
  protected readonly order = signal<StoreOrder | null>(null);
  protected readonly when = (iso: string) => WHEN.format(new Date(iso));

  constructor() {
    const ctx = inject(StoreContext);
    inject(Seo).apply({ title: `Pedido — ${ctx.store?.name ?? 'Loja'}`, description: 'Acompanhamento do pedido.', path: '/pedido', origin: ctx.origin, noindex: true });
    afterNextRender(() => void this.load());
  }

  private async load(): Promise<void> {
    const token = this.route.snapshot.fragment;
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
