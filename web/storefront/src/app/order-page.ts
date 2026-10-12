import { afterNextRender, Component, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { CheckoutApi, lastOrderToken, StoreOrder } from './checkout';
import { OrderDetails } from './order-details';
import { Seo } from './seo';
import { StoreContext } from './store';

/**
 * Acompanhamento do pedido pelo link do comprador (/pedido#token). O token fica no fragmento da URL, que o navegador
 * nunca envia ao servidor: a página sai do SSR em "Carregando…" e busca o pedido daqui.
 */
@Component({
  selector: 'app-order',
  imports: [RouterLink, OrderDetails],
  template: `
    @switch (state()) {
      @case ('loading') {
        <p class="muted">Carregando…</p>
      }
      @case ('notFound') {
        <h1>Pedido não encontrado</h1>
        <p>Confira se o endereço desta página está completo. <a routerLink="/">Voltar à loja</a></p>
        <p class="muted">Comprou aqui antes? Veja todos os seus pedidos em <a routerLink="/meus-pedidos">Meus pedidos</a>.</p>
      }
      @default {
        @if (order(); as o) {
          <app-order-details [order]="o" [token]="token()" (reload)="load()" />
        }
      }
    }
  `,
})
export class OrderPage {
  private readonly api = inject(CheckoutApi);
  private readonly route = inject(ActivatedRoute);

  protected readonly token = signal<string | null>(null);
  protected readonly state = signal<'loading' | 'notFound' | 'ok'>('loading');
  protected readonly order = signal<StoreOrder | null>(null);

  constructor() {
    const ctx = inject(StoreContext);
    inject(Seo).apply({ title: `Pedido — ${ctx.store?.name ?? 'Loja'}`, description: 'Acompanhamento do pedido.', path: '/pedido', origin: ctx.origin, noindex: true });
    afterNextRender(() => void this.load());
  }

  protected async load(): Promise<void> {
    // Volta do gateway chega sem o token: usa o último pedido feito neste navegador.
    const token = this.route.snapshot.fragment || lastOrderToken();
    this.token.set(token);
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
