import { CurrencyPipe } from '@angular/common';
import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { problemTitle } from '../../core/auth';
import { DELIVERY_LABELS, ORDER_STATUS_LABELS, OrderPage, OrdersApi, OrderStatus, OrderView } from './orders.api';

const WHEN = new Intl.DateTimeFormat('pt-BR', { timeZone: 'America/Sao_Paulo', dateStyle: 'short', timeStyle: 'short' });

/** Pedidos da loja (RF14). O painel unificado de todos os canais, com filtros e ações, é o RF17. */
@Component({
  selector: 'app-orders',
  imports: [FormsModule, RouterLink, CurrencyPipe],
  template: `
    <main class="card wide">
      <div class="header-row">
        <h1>Pedidos</h1>
        <select [(ngModel)]="status" (ngModelChange)="load(1)" aria-label="Situação">
          <option value="">Todas as situações</option>
          @for (s of statuses; track s) {
            <option [value]="s">{{ statusLabels[s] }}</option>
          }
        </select>
      </div>

      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }

      <table>
        <thead>
          <tr>
            <th>Nº</th>
            <th>Data</th>
            <th>Comprador</th>
            <th>Entrega</th>
            <th class="num">Peças</th>
            <th class="num">Total</th>
            <th>Situação</th>
          </tr>
        </thead>
        <tbody>
          @for (order of page()?.items ?? []; track order.id) {
            <tr>
              <td><button type="button" class="link" (click)="open(order.id)" [attr.aria-expanded]="selected()?.id === order.id">{{ order.number }}</button></td>
              <td>{{ when(order.placedAt) }}</td>
              <td>{{ order.buyerName }}</td>
              <td>{{ deliveryLabels[order.deliveryMethod] }}</td>
              <td class="num">{{ order.itemCount }}</td>
              <td class="num">{{ order.total | currency: 'BRL' : 'symbol' : '1.2-2' : 'pt-BR' }}</td>
              <td><span class="badge" [class.ok]="order.status === 'paid'">{{ statusLabels[order.status] }}</span></td>
            </tr>
          } @empty {
            <tr>
              <td colspan="7" class="muted">{{ loading() ? 'Carregando…' : 'Nenhum pedido ainda.' }}</td>
            </tr>
          }
        </tbody>
      </table>

      @if (page(); as p) {
        @if (p.total > p.pageSize) {
          <div class="pager">
            <button type="button" class="link" [disabled]="p.page <= 1" (click)="load(p.page - 1)">Anterior</button>
            <span class="muted">Página {{ p.page }} de {{ pages(p) }} · {{ p.total }} pedidos</span>
            <button type="button" class="link" [disabled]="p.page >= pages(p)" (click)="load(p.page + 1)">Próxima</button>
          </div>
        }
      }

      @if (selected(); as o) {
        <section class="order-detail" aria-live="polite">
          <h2>Pedido nº {{ o.number }} · {{ statusLabels[o.status] }}</h2>
          @if (o.status === 'pendingPayment') {
            <p class="muted">Peças reservadas até {{ when(o.paymentDeadline) }}; sem pagamento, o pedido é cancelado e as peças voltam ao estoque.</p>
          }
          @if (o.status === 'canceled') {
            <p class="muted">Cancelado em {{ when(o.canceledAt!) }}. {{ o.cancelReason }}</p>
          }
          <h3>Comprador</h3>
          <p>{{ o.buyer.name }} · CPF {{ cpf(o.buyer.cpf) }}<br />{{ o.buyer.email }} · {{ phone(o.buyer.phone) }}</p>
          <h3>{{ deliveryLabels[o.deliveryMethod] }}</h3>
          @if (o.address; as a) {
            <p>
              {{ a.street }}, {{ a.number }}{{ a.complement ? ' — ' + a.complement : '' }}<br />
              {{ a.district }} · {{ a.city }}/{{ a.state }} · CEP {{ a.postalCode.slice(0, 5) }}-{{ a.postalCode.slice(5) }}<br />
              <span class="muted">{{ o.carrier }} · {{ o.service }} ({{ o.deliveryDays }} dias úteis)</span>
            </p>
          } @else {
            <p class="pre">{{ o.pickupAddress }}</p>
          }
          <h3>Peças</h3>
          <table>
            <tbody>
              @for (item of o.items; track item.partId) {
                <tr>
                  <td><a [routerLink]="['/pecas', item.partId]">{{ item.internalCode }}</a></td>
                  <td>{{ item.title }}</td>
                  <td class="num">{{ item.quantity }} × {{ item.unitPrice | currency: 'BRL' : 'symbol' : '1.2-2' : 'pt-BR' }}</td>
                </tr>
              }
              <tr><td colspan="2">Frete</td><td class="num">{{ o.shippingTotal | currency: 'BRL' : 'symbol' : '1.2-2' : 'pt-BR' }}</td></tr>
              <tr><td colspan="2"><strong>Total</strong></td><td class="num"><strong>{{ o.total | currency: 'BRL' : 'symbol' : '1.2-2' : 'pt-BR' }}</strong></td></tr>
            </tbody>
          </table>
        </section>
      }
    </main>
  `,
})
export class OrdersPage implements OnInit {
  private readonly api = inject(OrdersApi);

  protected readonly statuses: OrderStatus[] = ['pendingPayment', 'paid', 'canceled'];
  protected readonly statusLabels = ORDER_STATUS_LABELS;
  protected readonly deliveryLabels = DELIVERY_LABELS;
  protected status: OrderStatus | '' = '';
  protected readonly page = signal<OrderPage | null>(null);
  protected readonly selected = signal<OrderView | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly when = (iso: string) => WHEN.format(new Date(iso));
  protected readonly cpf = (v: string) => `${v.slice(0, 3)}.${v.slice(3, 6)}.${v.slice(6, 9)}-${v.slice(9)}`;
  protected readonly phone = (v: string) => `(${v.slice(0, 2)}) ${v.slice(2, v.length - 4)}-${v.slice(-4)}`;

  async ngOnInit(): Promise<void> {
    await this.load(1);
  }

  protected pages(p: OrderPage): number {
    return Math.max(1, Math.ceil(p.total / p.pageSize));
  }

  protected async open(id: string): Promise<void> {
    if (this.selected()?.id === id) {
      this.selected.set(null);
      return;
    }
    try {
      this.selected.set(await this.api.get(id));
    } catch (error) {
      this.error.set(problemTitle(error));
    }
  }

  async load(pageNumber: number): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.page.set(await this.api.search(this.status, pageNumber));
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.loading.set(false);
    }
  }
}
