import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export type OrderStatus = 'pendingPayment' | 'paid' | 'canceled';
export type DeliveryMethod = 'shipping' | 'pickup';

export const ORDER_STATUS_LABELS: Record<OrderStatus, string> = {
  pendingPayment: 'Aguardando pagamento',
  paid: 'Pago',
  canceled: 'Cancelado',
};

export const DELIVERY_LABELS: Record<DeliveryMethod, string> = { shipping: 'Entrega', pickup: 'Retirada' };

export interface OrderSummary {
  id: string;
  number: number;
  origin: string;
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

export interface OrderView {
  id: string;
  number: number;
  status: OrderStatus;
  placedAt: string;
  paymentDeadline: string;
  buyer: { name: string; email: string; phone: string; cpf: string };
  deliveryMethod: DeliveryMethod;
  address: { postalCode: string; street: string; number: string; complement: string | null; district: string; city: string; state: string } | null;
  carrier: string | null;
  service: string | null;
  deliveryDays: number | null;
  pickupAddress: string | null;
  items: { partId: string; internalCode: string; title: string; unitPrice: number; quantity: number }[];
  itemsTotal: number;
  shippingTotal: number;
  total: number;
  canceledAt: string | null;
  cancelReason: string | null;
}

@Injectable({ providedIn: 'root' })
export class OrdersApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/painel/pedidos';

  search(status: OrderStatus | '', page = 1, pageSize = 25): Promise<OrderPage> {
    let params = new HttpParams().set('pagina', page).set('tamanho', pageSize);
    if (status) params = params.set('situacao', status);
    return firstValueFrom(this.http.get<OrderPage>(this.base, { params }));
  }

  get(id: string): Promise<OrderView> {
    return firstValueFrom(this.http.get<OrderView>(`${this.base}/${id}`));
  }
}
