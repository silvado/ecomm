import { CurrencyPipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { afterNextRender, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { CartApi, CartStore, CartView, ShippingQuote } from './cart';
import { CONDITION_LABELS, partPath, photoUrl } from './catalog';
import { Seo } from './seo';
import { StoreContext } from './store';

const PROBLEM_LABELS = {
  unavailable: 'Não está mais disponível e saiu do carrinho.',
  quantityReduced: 'Quantidade ajustada ao estoque.',
  noShippingDimensions: 'Só para retirada na loja.',
} as const;

/** Carrinho (RF14): itens conferidos no servidor e frete por CEP. A finalização vem com o checkout. */
@Component({
  selector: 'app-cart',
  imports: [CurrencyPipe, FormsModule, RouterLink],
  template: `
    <h1>Carrinho</h1>
    @if (!loaded()) {
      <p class="muted">Carregando…</p>
    } @else if (!view()?.lines?.length) {
      <p>Seu carrinho está vazio. <a routerLink="/busca">Buscar peças</a></p>
    } @else {
      <ul class="cart-lines">
        @for (line of view()!.lines; track line.partId) {
          <li [class.gone]="line.problem === 'unavailable'">
            @if (line.coverPhotoId) {
              <img [src]="thumb(line.coverPhotoId)" alt="" width="72" height="72" />
            }
            <div class="line-info">
              <a [routerLink]="path(line)">{{ line.title }}</a>
              <span class="muted">{{ conditionLabels[line.condition] }} · {{ line.unitPrice | currency: 'BRL' : 'symbol' : '1.2-2' }}</span>
              @if (line.problem) {
                <span class="problem">{{ problemLabels[line.problem] }}</span>
              }
            </div>
            @if (line.problem !== 'unavailable') {
              <label class="qty">
                <span class="sr-only">Quantidade</span>
                <select [ngModel]="line.quantity" (ngModelChange)="setQuantity(line.partId, $event)">
                  @for (n of quantities(line.available); track n) {
                    <option [ngValue]="n">{{ n }}</option>
                  }
                </select>
              </label>
              <strong>{{ line.unitPrice * line.quantity | currency: 'BRL' : 'symbol' : '1.2-2' }}</strong>
            }
            <button type="button" class="link" (click)="remove(line.partId)">Remover</button>
          </li>
        }
      </ul>

      <section class="shipping">
        <h2>Entrega</h2>
        <form class="cep" (submit)="quote($event)">
          <label>
            <span>CEP</span>
            <input name="cep" [(ngModel)]="cep" inputmode="numeric" maxlength="9" placeholder="00000-000" autocomplete="postal-code" />
          </label>
          <button type="submit" [disabled]="quoting()">{{ quoting() ? 'Calculando…' : 'Calcular frete' }}</button>
        </form>
        @if (quoteError()) {
          <p class="problem" role="alert">{{ quoteError() }}</p>
        }
        @if (shipping(); as q) {
          @if (q.message) {
            <p class="muted">{{ q.message }}</p>
          }
          <fieldset class="options">
            <legend class="sr-only">Forma de entrega</legend>
            @for (option of q.options; track option.serviceId) {
              <label>
                <input type="radio" name="frete" [value]="option.serviceId" [(ngModel)]="selected" />
                <span>{{ option.carrier }} · {{ option.service }} — até {{ option.deliveryDays }} dias úteis</span>
                <strong>{{ option.price | currency: 'BRL' : 'symbol' : '1.2-2' }}</strong>
              </label>
            }
            @if (q.pickup) {
              <label>
                <input type="radio" name="frete" value="pickup" [(ngModel)]="selected" />
                <span>Retirar na loja: <span class="pre">{{ q.pickup }}</span></span>
                <strong>Grátis</strong>
              </label>
            }
          </fieldset>
        }
      </section>

      <section class="totals">
        <p><span>Subtotal</span> <strong>{{ view()!.subtotal | currency: 'BRL' : 'symbol' : '1.2-2' }}</strong></p>
        @if (shippingPrice() !== null) {
          <p><span>Frete</span> <strong>{{ shippingPrice()! | currency: 'BRL' : 'symbol' : '1.2-2' }}</strong></p>
          <p class="total"><span>Total</span> <strong>{{ view()!.subtotal + shippingPrice()! | currency: 'BRL' : 'symbol' : '1.2-2' }}</strong></p>
        }
        <button type="button" disabled title="Finalização da compra em breve">Finalizar compra</button>
      </section>
    }
  `,
})
export class CartPage {
  private readonly cart = inject(CartStore);
  private readonly api = inject(CartApi);

  protected readonly conditionLabels = CONDITION_LABELS;
  protected readonly problemLabels = PROBLEM_LABELS;
  protected readonly loaded = signal(false);
  protected readonly view = signal<CartView | null>(null);
  protected readonly shipping = signal<ShippingQuote | null>(null);
  protected readonly quoting = signal(false);
  protected readonly quoteError = signal<string | null>(null);
  protected cep = '';
  protected readonly selected = signal('');
  protected readonly path = (line: { partId: string; title: string }) => partPath({ id: line.partId, title: line.title });
  protected readonly thumb = (id: string) => photoUrl(id, 300);

  /** Preço da opção escolhida (retirada = 0); nulo enquanto não há cotação/escolha. */
  protected readonly shippingPrice = computed(() => {
    const quote = this.shipping();
    const selected = this.selected();
    if (!quote || !selected) return null;
    if (selected === 'pickup') return 0;
    return quote.options.find((o) => o.serviceId === selected)?.price ?? null;
  });

  constructor() {
    const ctx = inject(StoreContext);
    inject(Seo).apply({ title: `Carrinho — ${ctx.store?.name ?? 'Loja'}`, description: 'Seu carrinho.', path: '/carrinho', origin: ctx.origin, noindex: true });
    // O carrinho vive no navegador: no servidor a página sai em "Carregando…".
    afterNextRender(() => void this.refresh());
  }

  protected quantities(available: number): number[] {
    return Array.from({ length: Math.min(Math.max(available, 1), 20) }, (_, i) => i + 1);
  }

  protected async setQuantity(partId: string, quantity: number): Promise<void> {
    this.cart.set(partId, quantity);
    await this.refresh();
  }

  protected async remove(partId: string): Promise<void> {
    this.cart.remove(partId);
    await this.refresh();
  }

  protected async quote(event: Event): Promise<void> {
    event.preventDefault();
    this.quoting.set(true);
    this.quoteError.set(null);
    try {
      const quote = await firstValueFrom(this.api.quote(this.cep, this.cart.list()));
      this.shipping.set(quote);
      this.selected.set(quote.options[0]?.serviceId ?? (quote.pickup ? 'pickup' : ''));
    } catch (error) {
      this.shipping.set(null);
      this.quoteError.set(error instanceof HttpErrorResponse && typeof error.error?.title === 'string' ? error.error.title : 'Não foi possível calcular o frete.');
    } finally {
      this.quoting.set(false);
    }
  }

  private async refresh(): Promise<void> {
    const items = this.cart.list();
    if (items.length === 0) {
      this.view.set({ lines: [], subtotal: 0 });
    } else {
      try {
        const view = await firstValueFrom(this.api.check(items));
        this.view.set(view);
        this.cart.reconcile(view.lines);
      } catch {
        this.view.set({ lines: [], subtotal: 0 });
      }
    }
    this.shipping.set(null); // itens mudaram: frete precisa ser recalculado
    this.loaded.set(true);
  }
}
