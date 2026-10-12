import { CurrencyPipe, DOCUMENT } from '@angular/common';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { afterNextRender, Component, inject, Injectable, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { firstValueFrom, Observable } from 'rxjs';
import { OrderStatus, StoreOrder } from './checkout';
import { formatWhen, OrderDetails } from './order-details';
import { Seo } from './seo';
import { StoreContext } from './store';

export interface CustomerOrderSummary {
  number: number;
  placedAt: string;
  status: OrderStatus;
  deliveryMethod: 'shipping' | 'pickup';
  itemCount: number;
  total: number;
  trackingCode: string | null;
}

export const STATUS_LABEL: Record<OrderStatus, string> = {
  pendingPayment: 'Aguardando pagamento',
  paid: 'Pago',
  canceled: 'Cancelado',
  needsReview: 'Em análise',
  shipped: 'Enviado',
  delivered: 'Entregue',
  pickedUp: 'Retirado',
  returned: 'Devolvido',
};

/** Área do cliente (RF16): a sessão é um cookie HttpOnly que só vai para /api/loja/cliente. */
@Injectable({ providedIn: 'root' })
export class CustomerApi {
  private readonly http = inject(HttpClient);

  requestLink(email: string): Observable<void> {
    return this.http.post<void>('/api/loja/cliente/acesso', { email });
  }

  startSession(token: string): Observable<{ email: string }> {
    return this.http.post<{ email: string }>('/api/loja/cliente/sessao', { token });
  }

  orders(): Observable<{ email: string; orders: CustomerOrderSummary[] }> {
    return this.http.get<{ email: string; orders: CustomerOrderSummary[] }>('/api/loja/cliente/pedidos');
  }

  order(number: number): Observable<StoreOrder> {
    return this.http.get<StoreOrder>(`/api/loja/cliente/pedidos/${number}`);
  }

  signOut(): Observable<void> {
    return this.http.post<void>('/api/loja/cliente/sair', {});
  }
}

const TOKEN = /^[A-Za-z0-9_-]{43}$/;

/**
 * Meus pedidos (RF16, Q39): sem senha — o comprador pede um link no e-mail das compras. O link chega como
 * /meus-pedidos#token (o fragmento não vai ao servidor nem aos logs) e abre uma sessão de 30 dias neste navegador.
 */
@Component({
  selector: 'app-my-orders',
  imports: [CurrencyPipe, FormsModule, RouterLink, OrderDetails],
  template: `
    @switch (state()) {
      @case ('loading') {
        <p class="muted">Carregando…</p>
      }
      @case ('signedOut') {
        <h1>Meus pedidos</h1>
        @if (linkProblem()) {
          <p class="problem" role="alert">{{ linkProblem() }}</p>
        }
        @if (sentTo()) {
          <p class="notice" role="status">
            Se houver pedidos com <strong>{{ sentTo() }}</strong> nesta loja, você vai receber um e-mail com o link de acesso.
            O link vale por 15 minutos e só pode ser usado uma vez. Confira também a caixa de spam.
          </p>
          <p><button type="button" class="link" (click)="sentTo.set(null)">Usar outro e-mail</button></p>
        } @else {
          <p>Informe o e-mail usado nas compras. Enviaremos um link para você ver seus pedidos, sem precisar de senha.</p>
          <form class="customer-access" (ngSubmit)="requestLink()">
            <label>
              E-mail
              <input type="email" name="email" [(ngModel)]="email" autocomplete="email" required maxlength="254" />
            </label>
            <button type="submit" [disabled]="sending()">{{ sending() ? 'Enviando…' : 'Receber link por e-mail' }}</button>
          </form>
          @if (requestError()) {
            <p class="problem" role="alert">{{ requestError() }}</p>
          }
        }
      }
      @case ('list') {
        <h1>Meus pedidos</h1>
        <p class="muted">
          Pedidos feitos com {{ sessionEmail() }}.
          <button type="button" class="link" (click)="signOut()">Sair</button>
        </p>
        @if (orders().length === 0) {
          <p>Nenhum pedido encontrado. <a routerLink="/">Voltar à loja</a></p>
        } @else {
          <ul class="customer-orders">
            @for (o of orders(); track o.number) {
              <li>
                <button type="button" class="customer-order" (click)="open(o.number)">
                  <strong>Pedido nº {{ o.number }}</strong>
                  <span>{{ when(o.placedAt) }} · {{ o.itemCount }} {{ o.itemCount === 1 ? 'peça' : 'peças' }}</span>
                  <span class="status" [attr.data-status]="o.status">{{ label[o.status] }}</span>
                  <strong>{{ o.total | currency: 'BRL' : 'symbol' : '1.2-2' }}</strong>
                </button>
              </li>
            }
          </ul>
        }
      }
      @case ('detail') {
        <p><button type="button" class="link" (click)="back()">← Meus pedidos</button></p>
        @if (detail(); as d) {
          <app-order-details [order]="d" (reload)="open(d.number)" />
        }
      }
    }
  `,
})
export class MyOrdersPage {
  private readonly api = inject(CustomerApi);
  private readonly route = inject(ActivatedRoute);
  private readonly document = inject(DOCUMENT);

  protected readonly state = signal<'loading' | 'signedOut' | 'list' | 'detail'>('loading');
  protected readonly sessionEmail = signal('');
  protected readonly orders = signal<CustomerOrderSummary[]>([]);
  protected readonly detail = signal<StoreOrder | null>(null);
  protected readonly linkProblem = signal<string | null>(null);
  protected readonly requestError = signal<string | null>(null);
  protected readonly sentTo = signal<string | null>(null);
  protected readonly sending = signal(false);
  protected readonly label = STATUS_LABEL;
  protected readonly when = formatWhen;
  protected email = '';

  constructor() {
    const ctx = inject(StoreContext);
    inject(Seo).apply({ title: `Meus pedidos — ${ctx.store?.name ?? 'Loja'}`, description: 'Seus pedidos nesta loja.', path: '/meus-pedidos', origin: ctx.origin, noindex: true });
    afterNextRender(() => void this.start());
  }

  private async start(): Promise<void> {
    const token = this.route.snapshot.fragment;
    if (token) {
      // Tira o token do endereço (e do histórico) antes de qualquer coisa: ele só vale uma vez.
      this.document.defaultView?.history.replaceState(null, '', this.document.location.pathname);
      if (!TOKEN.test(token)) {
        this.linkProblem.set('Link incompleto. Copie o endereço inteiro do e-mail ou peça um novo link.');
      } else {
        try {
          await firstValueFrom(this.api.startSession(token));
        } catch {
          this.linkProblem.set('Este link venceu ou já foi usado. Peça um novo link abaixo.');
        }
      }
    }
    await this.loadList();
  }

  private async loadList(): Promise<void> {
    try {
      const result = await firstValueFrom(this.api.orders());
      this.sessionEmail.set(result.email);
      this.orders.set(result.orders);
      this.state.set('list');
    } catch {
      this.state.set('signedOut');
    }
  }

  protected async requestLink(): Promise<void> {
    const email = this.email.trim();
    if (!/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(email)) {
      this.requestError.set('Informe um e-mail válido.');
      return;
    }
    this.sending.set(true);
    this.requestError.set(null);
    try {
      await firstValueFrom(this.api.requestLink(email));
      this.linkProblem.set(null);
      this.sentTo.set(email);
    } catch (error) {
      this.requestError.set(
        error instanceof HttpErrorResponse && error.status === 429
          ? 'Muitos pedidos de link em pouco tempo. Aguarde um minuto e tente de novo.'
          : 'Não foi possível enviar agora. Tente de novo em instantes.',
      );
    } finally {
      this.sending.set(false);
    }
  }

  protected async open(number: number): Promise<void> {
    try {
      this.detail.set(await firstValueFrom(this.api.order(number)));
      this.state.set('detail');
    } catch (error) {
      if (error instanceof HttpErrorResponse && error.status === 401) this.state.set('signedOut');
    }
  }

  protected back(): Promise<void> {
    this.detail.set(null);
    return this.loadList();
  }

  protected async signOut(): Promise<void> {
    try {
      await firstValueFrom(this.api.signOut());
    } finally {
      this.orders.set([]);
      this.sessionEmail.set('');
      this.state.set('signedOut');
    }
  }
}
