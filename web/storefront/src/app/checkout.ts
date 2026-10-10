import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { CartItem } from './cart';

export interface Buyer {
  name: string;
  email: string;
  phone: string;
  cpf: string;
}

export interface DeliveryAddress {
  postalCode: string;
  street: string;
  number: string;
  complement: string | null;
  district: string;
  city: string;
  state: string;
}

export interface CheckoutRequest {
  token: string;
  items: CartItem[];
  expectedTotal: number;
  buyer: Buyer;
  delivery: { method: 'shipping'; address: DeliveryAddress; serviceId: string } | { method: 'pickup'; address: null; serviceId: null };
}

export interface PlacedOrder {
  number: number;
  total: number;
  paymentDeadline: string;
}

export type OrderStatus = 'pendingPayment' | 'paid' | 'canceled';

export interface StoreOrder {
  number: number;
  status: OrderStatus;
  placedAt: string;
  paymentDeadline: string;
  buyerName: string;
  deliveryMethod: 'shipping' | 'pickup';
  address: DeliveryAddress | null;
  carrier: string | null;
  service: string | null;
  deliveryDays: number | null;
  pickupAddress: string | null;
  items: { partId: string; title: string; unitPrice: number; quantity: number }[];
  itemsTotal: number;
  shippingTotal: number;
  total: number;
  cancelReason: string | null;
}

export const UF = ['AC', 'AL', 'AP', 'AM', 'BA', 'CE', 'DF', 'ES', 'GO', 'MA', 'MT', 'MS', 'MG', 'PA', 'PB', 'PR', 'PE', 'PI', 'RJ', 'RN', 'RS', 'RO', 'RR', 'SC', 'SP', 'SE', 'TO'];

/**
 * Token do pedido: 32 bytes aleatórios em base64url, sorteados no navegador. Dá acesso ao acompanhamento e, repetido,
 * faz o servidor devolver o mesmo pedido (duplo clique, queda de rede) em vez de criar outro.
 */
export function newOrderToken(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(32));
  return btoa(String.fromCharCode(...bytes)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

/** CPF com dígitos verificadores corretos (o servidor confere de novo). */
export function validCpf(input: string): boolean {
  const cpf = input.replace(/\D/g, '');
  if (cpf.length !== 11 || /^(\d)\1{10}$/.test(cpf)) return false;
  const digit = (body: string) => {
    const sum = [...body].reduce((s, c, i) => s + Number(c) * (body.length + 1 - i), 0);
    return sum % 11 < 2 ? 0 : 11 - (sum % 11);
  };
  return digit(cpf.slice(0, 9)) === Number(cpf[9]) && digit(cpf.slice(0, 10)) === Number(cpf[10]);
}

@Injectable({ providedIn: 'root' })
export class CheckoutApi {
  private readonly http = inject(HttpClient);

  place(request: CheckoutRequest): Observable<PlacedOrder> {
    return this.http.post<PlacedOrder>('/api/loja/pedidos', request);
  }

  /** Token no corpo, nunca na URL. */
  find(token: string): Observable<StoreOrder> {
    return this.http.post<StoreOrder>('/api/loja/pedidos/consulta', { token });
  }
}
