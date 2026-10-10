import { HttpClient } from '@angular/common/http';
import { afterNextRender, computed, inject, Injectable, signal } from '@angular/core';
import { Observable } from 'rxjs';
import { PartCondition } from './catalog';

/** O que o navegador guarda: só peça e quantidade. Preço, estoque e frete vêm sempre do servidor (RF14). */
export interface CartItem {
  partId: string;
  quantity: number;
}

export type CartProblem = 'unavailable' | 'quantityReduced' | 'noShippingDimensions';

export interface CartLine {
  partId: string;
  title: string;
  condition: PartCondition;
  unitPrice: number;
  quantity: number;
  available: number;
  coverPhotoId: string | null;
  problem: CartProblem | null;
}

export interface CartView {
  lines: CartLine[];
  subtotal: number;
}

export interface ShippingOption {
  serviceId: string;
  carrier: string;
  service: string;
  price: number;
  deliveryDays: number;
}

export interface ShippingQuote {
  options: ShippingOption[];
  pickup: string | null;
  message: string | null;
}

const STORAGE_KEY = 'carrinho';
export const MAX_LINES = 50;

/**
 * Carrinho da loja no navegador (localStorage é por domínio, logo por loja). Lido só depois da hidratação: o HTML
 * do servidor e o primeiro desenho do navegador mostram o mesmo carrinho vazio, sem divergência.
 */
@Injectable({ providedIn: 'root' })
export class CartStore {
  private readonly items = signal<CartItem[]>([]);
  readonly list = this.items.asReadonly();
  readonly count = computed(() => this.items().reduce((sum, item) => sum + item.quantity, 0));

  constructor() {
    afterNextRender(() => this.items.set(read()));
  }

  add(partId: string, quantity = 1): void {
    const current = this.items();
    const existing = current.find((i) => i.partId === partId);
    if (!existing && current.length >= MAX_LINES) return;
    this.save(existing ? current.map((i) => (i.partId === partId ? { ...i, quantity: i.quantity + quantity } : i)) : [...current, { partId, quantity }]);
  }

  set(partId: string, quantity: number): void {
    this.save(quantity > 0 ? this.items().map((i) => (i.partId === partId ? { ...i, quantity } : i)) : this.items().filter((i) => i.partId !== partId));
  }

  remove(partId: string): void {
    this.save(this.items().filter((i) => i.partId !== partId));
  }

  /** Aplica o que o servidor decidiu (peça indisponível sai; quantidade reduzida ao estoque). */
  reconcile(lines: CartLine[]): void {
    const byId = new Map(lines.map((l) => [l.partId, l]));
    this.save(
      this.items()
        .map((i) => ({ ...i, quantity: byId.get(i.partId)?.problem === 'unavailable' ? 0 : Math.min(i.quantity, byId.get(i.partId)?.quantity ?? i.quantity) }))
        .filter((i) => i.quantity > 0),
    );
  }

  clear(): void {
    this.save([]);
  }

  private save(items: CartItem[]): void {
    this.items.set(items);
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(items));
    } catch {
      // Navegação privada/armazenamento bloqueado: o carrinho vale só nesta aba.
    }
  }
}

function read(): CartItem[] {
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? '[]');
    if (!Array.isArray(parsed)) return [];
    return parsed
      .filter((i): i is CartItem => typeof i?.partId === 'string' && Number.isInteger(i?.quantity) && i.quantity > 0)
      .slice(0, MAX_LINES);
  } catch {
    return [];
  }
}

@Injectable({ providedIn: 'root' })
export class CartApi {
  private readonly http = inject(HttpClient);

  check(items: CartItem[]): Observable<CartView> {
    return this.http.post<CartView>('/api/loja/carrinho', { items });
  }

  quote(cep: string, items: CartItem[]): Observable<ShippingQuote> {
    return this.http.post<ShippingQuote>('/api/loja/frete', { cep, items });
  }
}
