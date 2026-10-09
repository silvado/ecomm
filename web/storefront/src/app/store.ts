import { inject, Injectable, makeStateKey, REQUEST_CONTEXT, RESPONSE_INIT, TransferState } from '@angular/core';

export interface StoreTheme {
  primaryColor: string;
  onPrimaryColor: string;
  backgroundColor: string;
  textColor: string;
}

export interface StoreTexts {
  about: string;
  returnPolicy: string;
  footer: string;
}

export interface PublicStore {
  slug: string;
  name: string;
  theme: StoreTheme;
  texts: StoreTexts;
}

/** Resultado de resolver a loja pelo Host (RF04): aberta, inexistente, suspensa ou API fora do ar. */
export type StoreState =
  | { kind: 'open'; store: PublicStore }
  | { kind: 'not-found' }
  | { kind: 'unavailable' }
  | { kind: 'error' };

/** O servidor (server.ts) resolve a loja antes de renderizar e entrega por REQUEST_CONTEXT. */
export interface StoreRequestContext {
  store: StoreState;
}

const STORE_KEY = makeStateKey<StoreState>('store');

const STATUS: Record<StoreState['kind'], number> = { open: 200, 'not-found': 404, unavailable: 503, error: 503 };

/**
 * Loja da requisição. No servidor vem do REQUEST_CONTEXT e vai para o TransferState;
 * no navegador, a hidratação lê o TransferState — sem segunda chamada à API.
 */
@Injectable({ providedIn: 'root' })
export class StoreContext {
  readonly state: StoreState;

  constructor() {
    const transfer = inject(TransferState);
    const request = inject(REQUEST_CONTEXT, { optional: true }) as StoreRequestContext | null;

    if (request?.store) {
      this.state = request.store;
      transfer.set(STORE_KEY, this.state);
      const response = inject(RESPONSE_INIT, { optional: true });
      if (response) response.status = STATUS[this.state.kind];
    } else {
      this.state = transfer.get(STORE_KEY, { kind: 'error' });
    }
  }

  get store(): PublicStore | null {
    return this.state.kind === 'open' ? this.state.store : null;
  }
}
