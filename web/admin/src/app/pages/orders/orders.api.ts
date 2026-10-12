import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export type OrderStatus = 'pendingPayment' | 'paid' | 'canceled' | 'needsReview' | 'shipped' | 'delivered' | 'pickedUp' | 'returned';
export type ReturnReason = 'regret' | 'defect' | 'wrongPart' | 'other';
export type RefundMethod = 'gateway' | 'pix' | 'cash' | 'storeCredit' | 'other';
export type OrderOrigin = 'site' | 'counter';
export type CounterPaymentMethod = 'cash' | 'pix' | 'debitCard' | 'creditCard' | 'other';
export type DeliveryMethod = 'shipping' | 'pickup';
export type OrderActor = 'buyer' | 'user' | 'payment' | 'system';
export type CancellationSource = 'paymentExpired' | 'store';
export type OrderAction = 'aprovar' | 'cancelar' | 'enviar' | 'entregar' | 'retirar' | 'devolver';

export const ORDER_STATUS_LABELS: Record<OrderStatus, string> = {
  pendingPayment: 'Aguardando pagamento',
  paid: 'Pago',
  needsReview: 'Em análise',
  shipped: 'Enviado',
  delivered: 'Entregue',
  pickedUp: 'Retirado',
  returned: 'Devolvido',
  canceled: 'Cancelado',
};

export const RETURN_REASON_LABELS: Record<ReturnReason, string> = {
  regret: 'Arrependimento',
  defect: 'Defeito / garantia',
  wrongPart: 'Peça errada ou não serviu',
  other: 'Outro motivo',
};

export const REFUND_METHOD_LABELS: Record<RefundMethod, string> = {
  gateway: 'Estorno no gateway (cartão/Pix do site)',
  pix: 'Pix',
  cash: 'Dinheiro',
  storeCredit: 'Crédito na loja',
  other: 'Outra',
};

export const ORDER_ORIGIN_LABELS: Record<OrderOrigin, string> = { site: 'Loja virtual', counter: 'Balcão' };

export const PAYMENT_METHOD_LABELS: Record<CounterPaymentMethod, string> = {
  cash: 'Dinheiro',
  pix: 'Pix',
  debitCard: 'Cartão de débito',
  creditCard: 'Cartão de crédito',
  other: 'Outra',
};

export const ACTOR_LABELS: Record<OrderActor, string> = {
  buyer: 'Comprador',
  user: 'Loja',
  payment: 'Pagamento',
  system: 'Sistema',
};

export const CANCELED_BY_LABELS: Record<CancellationSource, string> = {
  paymentExpired: 'pelo prazo de pagamento',
  store: 'pela loja',
};

export const PAYMENT_STATUS_LABELS: Record<string, string> = {
  pending: 'Pendente',
  approved: 'Aprovado',
  rejected: 'Recusado',
  refunded: 'Estornado',
  canceled: 'Cancelado',
};

export const DELIVERY_LABELS: Record<DeliveryMethod, string> = { shipping: 'Entrega', pickup: 'Retirada' };

/** Ações que a loja pode fazer em cada situação (as mesmas regras do domínio; a API recusa o resto). */
export function availableActions(status: OrderStatus, delivery: DeliveryMethod, origin: OrderOrigin = 'site', hasReturns = false): OrderAction[] {
  switch (status) {
    case 'needsReview':
      return ['aprovar', 'cancelar'];
    case 'pendingPayment':
      return ['cancelar'];
    case 'paid':
      return delivery === 'shipping' ? ['enviar', 'cancelar'] : ['retirar', 'cancelar'];
    case 'shipped':
      return ['entregar', 'cancelar'];
    case 'delivered':
      return ['devolver'];
    case 'pickedUp':
      // Venda de balcão nasce retirada: cancelar corrige um lançamento errado (RF18), enquanto não houver devolução (RF20).
      return origin === 'counter' && !hasReturns ? ['devolver', 'cancelar'] : ['devolver'];
    default:
      return [];
  }
}

export interface OrderFilters {
  statuses: OrderStatus[];
  origin: OrderOrigin | '';
  from: string;
  to: string;
  text: string;
}

export interface OrderSummary {
  id: string;
  number: number;
  origin: OrderOrigin;
  status: OrderStatus;
  placedAt: string;
  buyerName: string;
  deliveryMethod: DeliveryMethod;
  itemCount: number;
  total: number;
}

export interface OrderPage {
  items: OrderSummary[];
  total: number;
  page: number;
  pageSize: number;
}

export interface OrderHistory {
  at: string;
  from: OrderStatus | null;
  to: OrderStatus;
  actor: OrderActor;
  userEmail: string | null;
  note: string | null;
}

export interface OrderView {
  id: string;
  number: number;
  origin: OrderOrigin;
  externalId: string | null;
  status: OrderStatus;
  placedAt: string;
  paymentDeadline: string;
  /** Na venda de balcão, e-mail, telefone e CPF podem vir vazios e o documento pode ser CNPJ. */
  buyer: { name: string; email: string; phone: string; cpf: string; cnpj: string | null };
  deliveryMethod: DeliveryMethod;
  address: { postalCode: string; street: string; number: string; complement: string | null; district: string; city: string; state: string } | null;
  carrier: string | null;
  service: string | null;
  deliveryDays: number | null;
  pickupAddress: string | null;
  items: { partId: string; internalCode: string; title: string; unitPrice: number; quantity: number; returnedQuantity: number }[];
  itemsTotal: number;
  shippingTotal: number;
  total: number;
  canceledAt: string | null;
  cancelReason: string | null;
  canceledBy: CancellationSource | null;
  paidAt: string | null;
  reviewReason: string | null;
  trackingCode: string | null;
  shippedAt: string | null;
  deliveredAt: string | null;
  paymentMethod: CounterPaymentMethod | null;
  payments: { gateway: string; externalId: string; status: string; amount: number; method: string | null; updatedAt: string }[];
  history: OrderHistory[];
  returnedAt: string | null;
  returns: OrderReturn[];
}

export interface OrderReturn {
  at: string;
  reason: ReturnReason;
  note: string | null;
  refundAmount: number | null;
  refundMethod: RefundMethod | null;
  userEmail: string | null;
  lines: { partId: string; internalCode: string; title: string; quantity: number; restock: boolean }[];
}

export interface ReturnRequest {
  items: { partId: string; quantity: number; restock: boolean }[];
  reason: ReturnReason;
  note: string | null;
  refundAmount: number | null;
  refundMethod: RefundMethod | null;
}

export interface CounterSaleRequest {
  token: string;
  items: { partId: string; quantity: number; unitPrice: number }[];
  paymentMethod: CounterPaymentMethod;
  customer: { name: string; document: string; phone: string };
}

@Injectable({ providedIn: 'root' })
export class OrdersApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/painel/pedidos';

  search(filters: OrderFilters, page = 1, pageSize = 25): Promise<OrderPage> {
    let params = new HttpParams().set('pagina', page).set('tamanho', pageSize);
    if (filters.statuses.length) params = params.set('situacao', filters.statuses.join(','));
    if (filters.origin) params = params.set('origem', filters.origin);
    if (filters.from) params = params.set('de', filters.from);
    if (filters.to) params = params.set('ate', filters.to);
    if (filters.text.trim()) params = params.set('busca', filters.text.trim());
    return firstValueFrom(this.http.get<OrderPage>(this.base, { params }));
  }

  get(id: string): Promise<OrderView> {
    return firstValueFrom(this.http.get<OrderView>(`${this.base}/${id}`));
  }

  /** Venda de balcão (RF18): devolve o pedido criado; o mesmo token enviado de novo devolve a mesma venda. */
  sell(request: CounterSaleRequest): Promise<OrderView> {
    return firstValueFrom(this.http.post<OrderView>('/api/painel/balcao', request));
  }

  /** Devolução (RF20): parcial ou total; devolve o pedido atualizado. */
  returnItems(id: string, request: ReturnRequest): Promise<OrderView> {
    return firstValueFrom(this.http.post<OrderView>(`${this.base}/${id}/devolucoes`, request));
  }

  /** Executa a ação e devolve o pedido já atualizado (400/404/409 com a mensagem em "title"). */
  act(id: string, action: OrderAction, body: { motivo?: string; rastreio?: string } = {}): Promise<OrderView> {
    return firstValueFrom(this.http.post<OrderView>(`${this.base}/${id}/${action}`, body));
  }
}
