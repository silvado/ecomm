import { CurrencyPipe } from '@angular/common';
import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { OrderReturnForm } from './order-return';
import { problemTitle } from '../../core/auth';
import {
  ACTOR_LABELS,
  availableActions,
  CANCELED_BY_LABELS,
  DELIVERY_LABELS,
  ORDER_ORIGIN_LABELS,
  ORDER_STATUS_LABELS,
  OrderAction,
  OrderFilters,
  OrderHistory,
  OrderOrigin,
  OrderPage,
  OrdersApi,
  OrderStatus,
  OrderView,
  PAYMENT_METHOD_LABELS,
  PAYMENT_STATUS_LABELS,
  REFUND_METHOD_LABELS,
  RETURN_REASON_LABELS,
} from './orders.api';

const WHEN = new Intl.DateTimeFormat('pt-BR', { timeZone: 'America/Sao_Paulo', dateStyle: 'short', timeStyle: 'short' });

const ACTION_LABELS: Record<OrderAction, string> = {
  aprovar: 'Aprovar pedido',
  cancelar: 'Cancelar pedido',
  enviar: 'Marcar como enviado',
  entregar: 'Marcar como entregue',
  retirar: 'Marcar como retirado',
  devolver: 'Registrar devolução',
};

/**
 * Pedidos de todos os canais num lugar só (RF17): filtros guardados na URL, detalhe com histórico e as ações da loja
 * (aprovar análise, enviar, entregar, retirar, cancelar). Cada ação passa por uma confirmação antes de ir à API.
 */
@Component({
  selector: 'app-orders',
  imports: [FormsModule, RouterLink, CurrencyPipe, OrderReturnForm],
  template: `
    <main class="card wide">
      <h1>Pedidos</h1>

      <div class="filters">
        <input type="search" placeholder="Nº do pedido, nome, e-mail, CPF ou CNPJ" [(ngModel)]="filters.text" (ngModelChange)="textChanged()" aria-label="Buscar pedidos" />
        <select [(ngModel)]="filters.origin" (ngModelChange)="load(1)" aria-label="Origem">
          <option value="">Todas as origens</option>
          @for (o of origins; track o) {
            <option [value]="o">{{ originLabels[o] }}</option>
          }
        </select>
        <label>De <input type="date" [(ngModel)]="filters.from" (ngModelChange)="load(1)" aria-label="Feitos a partir de" /></label>
        <label>Até <input type="date" [(ngModel)]="filters.to" (ngModelChange)="load(1)" aria-label="Feitos até" /></label>
      </div>
      <fieldset class="status-filter">
        <legend>Situação</legend>
        @for (s of statuses; track s) {
          <label><input type="checkbox" [checked]="filters.statuses.includes(s)" (change)="toggleStatus(s)" [attr.data-status]="s" /> {{ statusLabels[s] }}</label>
        }
        @if (filtered()) {
          <button type="button" class="link" (click)="clear()">Limpar filtros</button>
        }
      </fieldset>

      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }

      <table>
        <thead>
          <tr>
            <th>Nº</th>
            <th>Data</th>
            <th>Comprador</th>
            <th>Origem</th>
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
              <td>{{ originLabels[order.origin] ?? order.origin }}</td>
              <td>{{ order.origin === 'counter' ? '—' : deliveryLabels[order.deliveryMethod] }}</td>
              <td class="num">{{ order.itemCount }}</td>
              <td class="num">{{ order.total | currency: 'BRL' : 'symbol' : '1.2-2' : 'pt-BR' }}</td>
              <td><span class="badge" [class.ok]="done(order.status)" [class.warn]="order.status === 'needsReview'">{{ statusLabels[order.status] }}</span></td>
            </tr>
          } @empty {
            <tr>
              <td colspan="8" class="muted">{{ loading() ? 'Carregando…' : filtered() ? 'Nenhum pedido com esses filtros.' : 'Nenhum pedido ainda.' }}</td>
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
          @if (o.externalId) {
            <p class="muted">{{ originLabels[o.origin] ?? o.origin }} · id {{ o.externalId }}</p>
          }
          @if (o.status === 'pendingPayment') {
            <p class="muted">Peças reservadas até {{ when(o.paymentDeadline) }}; sem pagamento, o pedido é cancelado e as peças voltam ao estoque.</p>
          }
          @if (o.status === 'canceled') {
            <p class="muted">Cancelado {{ o.canceledBy ? canceledByLabels[o.canceledBy] : '' }} em {{ when(o.canceledAt!) }}. {{ o.cancelReason }}</p>
          }
          @if (o.status === 'needsReview') {
            <p class="warn" role="status">{{ o.reviewReason }}</p>
          }
          @if (o.paidAt) {
            <p class="muted">Pago em {{ when(o.paidAt) }}{{ o.paymentMethod ? ' · ' + paymentMethodLabels[o.paymentMethod] : '' }}.</p>
          }
          @if (o.shippedAt) {
            <p class="muted">Enviado em {{ when(o.shippedAt) }}{{ o.trackingCode ? ' · rastreio ' + o.trackingCode : ' · sem código de rastreio' }}.</p>
          }
          @if (o.returnedAt) {
            <p class="muted">Devolvido por inteiro em {{ when(o.returnedAt) }}.</p>
          }
          @if (o.deliveredAt && o.origin !== 'counter') {
            <p class="muted">{{ o.status === 'pickedUp' ? 'Retirado' : 'Entregue' }} em {{ when(o.deliveredAt) }}.</p>
          }

          @if (actions(o).length) {
            <div class="order-actions">
              @for (a of actions(o); track a) {
                <button type="button" [class.danger]="a === 'cancelar'" [attr.data-action]="a" (click)="choose(a)" [disabled]="busy()">{{ actionLabels[a] }}</button>
              }
            </div>
          }

          @if (pending() === 'devolver') {
            <app-order-return [order]="o" (done)="returned($event)" (closed)="pending.set(null)" />
          } @else if (pending(); as a) {
            <form class="confirm" (ngSubmit)="confirm(o, a)" data-confirm>
              <p><strong>{{ actionLabels[a] }} nº {{ o.number }}?</strong> {{ explain(o, a) }}</p>
              @if (a === 'cancelar') {
                <label>
                  Motivo (o comprador vê)
                  <textarea name="motivo" rows="2" maxlength="200" required [(ngModel)]="reason"></textarea>
                </label>
              }
              @if (a === 'enviar') {
                <label>
                  Código de rastreio (opcional)
                  <input name="rastreio" maxlength="60" [(ngModel)]="tracking" />
                </label>
              }
              <div class="order-actions">
                <button type="submit" [class.danger]="a === 'cancelar'" [disabled]="busy() || (a === 'cancelar' && !reason.trim())">Confirmar</button>
                <button type="button" class="link" (click)="pending.set(null)" [disabled]="busy()">Voltar</button>
              </div>
            </form>
          }
          @if (actionError()) {
            <p class="error" role="alert">{{ actionError() }}</p>
          }

          @if (o.payments.length) {
            <h3>Pagamentos</h3>
            <ul>
              @for (p of o.payments; track p.externalId) {
                <li>{{ p.gateway }} · {{ paymentLabels[p.status] ?? p.status }} · {{ p.amount | currency: 'BRL' : 'symbol' : '1.2-2' : 'pt-BR' }} · {{ when(p.updatedAt) }}</li>
              }
            </ul>
          }
          <h3>{{ o.origin === 'counter' ? 'Cliente' : 'Comprador' }}</h3>
          <p>
            {{ o.buyer.name }}{{ document(o) }}
            @if (contact(o); as c) {
              <br />{{ c }}
            }
          </p>
          @if (o.origin === 'counter') {
            <h3>Venda no balcão</h3>
            <p class="muted">As peças saíram na hora da venda.</p>
          } @else {
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
          }
          <h3>Peças</h3>
          <table>
            <tbody>
              @for (item of o.items; track item.partId) {
                <tr>
                  <td><a [routerLink]="['/pecas', item.partId]">{{ item.internalCode }}</a></td>
                  <td>{{ item.title }}</td>
                  <td class="num">{{ item.quantity }} × {{ item.unitPrice | currency: 'BRL' : 'symbol' : '1.2-2' : 'pt-BR' }}@if (item.returnedQuantity) {<br /><small class="muted">{{ item.returnedQuantity }} devolvida(s)</small>}</td>
                </tr>
              }
              <tr><td colspan="2">Frete</td><td class="num">{{ o.shippingTotal | currency: 'BRL' : 'symbol' : '1.2-2' : 'pt-BR' }}</td></tr>
              <tr><td colspan="2"><strong>Total</strong></td><td class="num"><strong>{{ o.total | currency: 'BRL' : 'symbol' : '1.2-2' : 'pt-BR' }}</strong></td></tr>
            </tbody>
          </table>

          @if (o.returns.length) {
            <h3>Devoluções</h3>
            <ul data-returns>
              @for (r of o.returns; track $index) {
                <li>
                  {{ when(r.at) }} · {{ returnReasonLabels[r.reason] }} ·
                  @for (l of r.lines; track l.partId; let last = $last) {
                    {{ l.quantity }}× {{ l.internalCode }} ({{ l.restock ? 'voltou ao estoque' : 'avariada' }}){{ last ? '' : ', ' }}
                  }
                  @if (r.refundAmount != null) {
                    · reembolso {{ r.refundAmount | currency: 'BRL' : 'symbol' : '1.2-2' : 'pt-BR' }}{{ r.refundMethod ? ' (' + refundMethodLabels[r.refundMethod] + ')' : '' }}
                  }
                  · {{ r.userEmail ?? 'Loja' }}{{ r.note ? ' · ' + r.note : '' }}
                </li>
              }
            </ul>
          }

          <h3>Histórico</h3>
          <ol class="history">
            @for (h of o.history; track $index) {
              <li>
                <span class="muted">{{ when(h.at) }}</span>
                {{ h.from ? statusLabels[h.from] + ' → ' : '' }}<strong>{{ statusLabels[h.to] }}</strong>
                · {{ who(h) }}
                @if (h.note) {
                  <br /><span class="pre">{{ h.note }}</span>
                }
              </li>
            }
          </ol>
        </section>
      }
    </main>
  `,
})
export class OrdersPage implements OnInit {
  private readonly api = inject(OrdersApi);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private debounce?: ReturnType<typeof setTimeout>;

  protected readonly statuses: OrderStatus[] = ['pendingPayment', 'needsReview', 'paid', 'shipped', 'delivered', 'pickedUp', 'returned', 'canceled'];
  protected readonly origins: OrderOrigin[] = ['site', 'counter'];
  protected readonly paymentLabels = PAYMENT_STATUS_LABELS;
  protected readonly statusLabels = ORDER_STATUS_LABELS;
  protected readonly originLabels: Record<string, string> = ORDER_ORIGIN_LABELS;
  protected readonly deliveryLabels = DELIVERY_LABELS;
  protected readonly canceledByLabels = CANCELED_BY_LABELS;
  protected readonly actionLabels = ACTION_LABELS;
  protected readonly paymentMethodLabels = PAYMENT_METHOD_LABELS;
  protected readonly returnReasonLabels = RETURN_REASON_LABELS;
  protected readonly refundMethodLabels = REFUND_METHOD_LABELS;
  protected filters: OrderFilters = { statuses: [], origin: '', from: '', to: '', text: '' };
  protected reason = '';
  protected tracking = '';
  protected readonly page = signal<OrderPage | null>(null);
  protected readonly selected = signal<OrderView | null>(null);
  protected readonly pending = signal<OrderAction | null>(null);
  protected readonly busy = signal(false);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly actionError = signal<string | null>(null);
  protected readonly when = (iso: string) => WHEN.format(new Date(iso));
  protected readonly cpf = (v: string) => `${v.slice(0, 3)}.${v.slice(3, 6)}.${v.slice(6, 9)}-${v.slice(9)}`;
  protected readonly phone = (v: string) => `(${v.slice(0, 2)}) ${v.slice(2, v.length - 4)}-${v.slice(-4)}`;
  protected readonly done = (s: OrderStatus) => s === 'paid' || s === 'delivered' || s === 'pickedUp';
  protected readonly actions = (o: OrderView) => availableActions(o.status, o.deliveryMethod, o.origin, o.returns.length > 0);
  protected readonly cnpj = (v: string) => (v.length === 14 ? `${v.slice(0, 2)}.${v.slice(2, 5)}.${v.slice(5, 8)}/${v.slice(8, 12)}-${v.slice(12)}` : v);
  protected readonly contact = (o: OrderView) => [o.buyer.email, o.buyer.phone ? this.phone(o.buyer.phone) : ''].filter((v) => v).join(' · ');
  protected readonly document = (o: OrderView) => (o.buyer.cnpj ? ' · CNPJ ' + this.cnpj(o.buyer.cnpj) : o.buyer.cpf ? ' · CPF ' + this.cpf(o.buyer.cpf) : '');

  async ngOnInit(): Promise<void> {
    const q = this.route.snapshot.queryParamMap;
    this.filters = {
      statuses: (q.get('situacao') ?? '').split(',').filter((s): s is OrderStatus => (this.statuses as string[]).includes(s)),
      origin: (this.origins as string[]).includes(q.get('origem') ?? '') ? (q.get('origem') as OrderOrigin) : '',
      from: q.get('de') ?? '',
      to: q.get('ate') ?? '',
      text: q.get('busca') ?? '',
    };
    await this.load(Number(q.get('pagina')) || 1);
  }

  protected filtered(): boolean {
    const f = this.filters;
    return !!(f.statuses.length || f.origin || f.from || f.to || f.text.trim());
  }

  protected toggleStatus(status: OrderStatus): void {
    const set = this.filters.statuses;
    this.filters.statuses = set.includes(status) ? set.filter((s) => s !== status) : this.statuses.filter((s) => s === status || set.includes(s));
    void this.load(1);
  }

  protected textChanged(): void {
    clearTimeout(this.debounce);
    this.debounce = setTimeout(() => void this.load(1), 300);
  }

  protected clear(): void {
    this.filters = { statuses: [], origin: '', from: '', to: '', text: '' };
    void this.load(1);
  }

  protected pages(p: OrderPage): number {
    return Math.max(1, Math.ceil(p.total / p.pageSize));
  }

  protected async open(id: string): Promise<void> {
    this.pending.set(null);
    this.actionError.set(null);
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

  protected choose(action: OrderAction): void {
    this.reason = '';
    this.tracking = '';
    this.actionError.set(null);
    this.pending.set(action);
  }

  protected explain(o: OrderView, action: OrderAction): string {
    switch (action) {
      case 'aprovar':
        return 'As peças são baixadas do estoque agora; se alguma já foi vendida, a aprovação é recusada.';
      case 'cancelar':
        if (o.origin === 'counter') return 'Para corrigir uma venda lançada errado: as peças voltam ao estoque. Devolva o dinheiro ao cliente, se for o caso.';
        return o.status === 'pendingPayment'
          ? 'As peças reservadas voltam a ficar disponíveis.'
          : 'As peças voltam ao estoque. O estorno do pagamento, se houver, é feito no gateway.';
      case 'enviar':
        return 'O comprador passa a ver o pedido como enviado, com o rastreio, se informado.';
      case 'entregar':
        return 'Depois de entregue, o pedido não pode mais ser cancelado pelo painel.';
      case 'retirar':
        return 'Confirme que o comprador retirou as peças no balcão.';
      case 'devolver':
        return '';
    }
  }

  protected async confirm(o: OrderView, action: OrderAction): Promise<void> {
    if (action === 'cancelar' && !this.reason.trim()) return;
    this.busy.set(true);
    this.actionError.set(null);
    try {
      const body = action === 'cancelar' ? { motivo: this.reason.trim() } : action === 'enviar' ? { rastreio: this.tracking.trim() } : {};
      const updated = await this.api.act(o.id, action, body);
      this.selected.set(updated);
      this.pending.set(null);
      this.page.update((p) => p && { ...p, items: p.items.map((i) => (i.id === updated.id ? { ...i, status: updated.status } : i)) });
    } catch (error) {
      this.actionError.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }

  /** Devolução registrada: mostra o pedido atualizado e a nova situação na lista. */
  protected returned(updated: OrderView): void {
    this.selected.set(updated);
    this.pending.set(null);
    this.page.update((p) => p && { ...p, items: p.items.map((i) => (i.id === updated.id ? { ...i, status: updated.status } : i)) });
  }

  protected who(h: OrderHistory): string {
    return h.actor === 'user' && h.userEmail ? h.userEmail : ACTOR_LABELS[h.actor];
  }

  async load(pageNumber: number): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    const f = this.filters;
    void this.router.navigate([], {
      relativeTo: this.route,
      replaceUrl: true,
      queryParams: {
        situacao: f.statuses.join(',') || null,
        origem: f.origin || null,
        de: f.from || null,
        ate: f.to || null,
        busca: f.text.trim() || null,
        pagina: pageNumber > 1 ? pageNumber : null,
      },
    });
    try {
      this.page.set(await this.api.search(f, pageNumber));
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.loading.set(false);
    }
  }
}
