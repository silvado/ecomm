import { CurrencyPipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { afterNextRender, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { CartApi, CartStore, CartView, ShippingQuote } from './cart';
import { CONDITION_LABELS, partPath, photoUrl } from './catalog';
import { Buyer, CheckoutApi, CheckoutRequest, newOrderToken, UF, validCpf } from './checkout';
import { Seo } from './seo';
import { StoreContext } from './store';

function problemTitle(error: unknown): string | null {
  return error instanceof HttpErrorResponse && typeof error.error?.title === 'string' ? error.error.title : null;
}

const PROBLEM_LABELS = {
  unavailable: 'Não está mais disponível e saiu do carrinho.',
  quantityReduced: 'Quantidade ajustada ao estoque.',
  noShippingDimensions: 'Só para retirada na loja.',
} as const;

/** Carrinho e checkout como convidado (RF14): itens conferidos no servidor, frete por CEP e pedido com as peças reservadas. */
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
        <button type="button" [disabled]="shippingPrice() === null" (click)="checkingOut.set(true)">Finalizar compra</button>
        @if (shippingPrice() === null) {
          <p class="muted">Calcule o frete ou escolha a retirada para continuar.</p>
        }
        @if (placeError()) {
          <p class="problem" role="alert">{{ placeError() }}</p>
        }
      </section>

      @if (checkingOut() && shippingPrice() !== null) {
        <form class="checkout" (submit)="place($event)" novalidate>
          <h2>Seus dados</h2>
          <label><span>Nome completo</span><input name="name" [(ngModel)]="buyer.name" autocomplete="name" maxlength="120" required /></label>
          <label><span>E-mail</span><input name="email" type="email" [(ngModel)]="buyer.email" autocomplete="email" maxlength="254" required /></label>
          <label><span>Telefone com DDD</span><input name="phone" type="tel" [(ngModel)]="buyer.phone" autocomplete="tel-national" inputmode="tel" maxlength="16" required /></label>
          <label><span>CPF</span><input name="cpf" [(ngModel)]="buyer.cpf" inputmode="numeric" maxlength="14" required /></label>

          @if (selected() !== 'pickup') {
            <h2>Endereço de entrega</h2>
            <p class="muted">CEP {{ quotedCep() }}. Para outro CEP, calcule o frete de novo.</p>
            <label><span>Rua</span><input name="street" [(ngModel)]="address.street" autocomplete="address-line1" maxlength="120" required /></label>
            <div class="row">
              <label><span>Número</span><input name="number" [(ngModel)]="address.number" maxlength="20" required /></label>
              <label><span>Complemento</span><input name="complement" [(ngModel)]="address.complement" autocomplete="address-line2" maxlength="60" /></label>
            </div>
            <label><span>Bairro</span><input name="district" [(ngModel)]="address.district" maxlength="60" required /></label>
            <div class="row">
              <label><span>Cidade</span><input name="city" [(ngModel)]="address.city" autocomplete="address-level2" maxlength="60" required /></label>
              <label><span>UF</span>
                <select name="state" [(ngModel)]="address.state" autocomplete="address-level1" required>
                  <option value="">—</option>
                  @for (uf of states; track uf) {
                    <option [value]="uf">{{ uf }}</option>
                  }
                </select>
              </label>
            </div>
          }

          <p class="muted">Usamos estes dados só para este pedido: entrega e nota fiscal.</p>
          <button type="submit" [disabled]="placing()">{{ placing() ? 'Enviando…' : 'Confirmar pedido' }}</button>
        </form>
      }
    }
  `,
})
export class CartPage {
  private readonly cart = inject(CartStore);
  private readonly api = inject(CartApi);
  private readonly checkout = inject(CheckoutApi);
  private readonly router = inject(Router);
  /** Mantido entre tentativas que não criaram pedido (erro de rede, dados inválidos): repetir não duplica. */
  private token: string | null = null;

  protected readonly states = UF;
  protected readonly checkingOut = signal(false);
  protected readonly placing = signal(false);
  protected readonly placeError = signal<string | null>(null);
  /** CEP da última cotação: o endereço de entrega é sempre o do frete cotado. */
  protected readonly quotedCep = signal('');
  protected readonly buyer: Buyer = { name: '', email: '', phone: '', cpf: '' };
  protected readonly address = { street: '', number: '', complement: '', district: '', city: '', state: '' };

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
      const digits = this.cep.replace(/\D/g, '');
      this.quotedCep.set(`${digits.slice(0, 5)}-${digits.slice(5)}`);
      this.shipping.set(quote);
      this.selected.set(quote.options[0]?.serviceId ?? (quote.pickup ? 'pickup' : ''));
    } catch (error) {
      this.shipping.set(null);
      this.quoteError.set(problemTitle(error) ?? 'Não foi possível calcular o frete.');
    } finally {
      this.quoting.set(false);
    }
  }

  protected async place(event: Event): Promise<void> {
    event.preventDefault();
    const view = this.view();
    const shippingPrice = this.shippingPrice();
    const invalid = this.validate();
    this.placeError.set(invalid);
    if (invalid || !view || shippingPrice === null) return;

    const selected = this.selected();
    const a = this.address;
    const request: CheckoutRequest = {
      token: (this.token ??= newOrderToken()),
      items: this.cart.list(),
      expectedTotal: Math.round((view.subtotal + shippingPrice) * 100) / 100,
      buyer: { ...this.buyer },
      delivery:
        selected === 'pickup'
          ? { method: 'pickup', address: null, serviceId: null }
          : {
              method: 'shipping',
              serviceId: selected,
              address: { postalCode: this.quotedCep(), street: a.street, number: a.number, complement: a.complement || null, district: a.district, city: a.city, state: a.state },
            },
    };

    this.placing.set(true);
    try {
      await firstValueFrom(this.checkout.place(request));
      this.token = null;
      this.cart.clear();
      await this.router.navigate(['/pedido'], { fragment: request.token });
    } catch (error) {
      this.placeError.set(problemTitle(error) ?? 'Não foi possível enviar o pedido. Confira sua conexão e tente de novo.');
      // Preço, estoque ou frete mudou: recarrega o carrinho e pede nova cotação antes de confirmar.
      if (error instanceof HttpErrorResponse && error.status === 409) await this.refresh();
    } finally {
      this.placing.set(false);
    }
  }

  /** Mesmas regras do servidor, para avisar antes de enviar. */
  private validate(): string | null {
    const b = this.buyer;
    if (b.name.trim().length < 3) return 'Informe o nome completo.';
    if (!/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(b.email.trim())) return 'Informe um e-mail válido.';
    if (!/^[1-9]\d{9,10}$/.test(b.phone.replace(/\D/g, ''))) return 'Informe o telefone com DDD.';
    if (!validCpf(b.cpf)) return 'CPF inválido.';
    if (this.selected() === 'pickup') return null;
    const a = this.address;
    if (!a.street.trim()) return 'Informe a rua.';
    if (!a.number.trim()) return 'Informe o número (ou "s/n").';
    if (!a.district.trim()) return 'Informe o bairro.';
    if (!a.city.trim()) return 'Informe a cidade.';
    if (!UF.includes(a.state)) return 'Informe a UF.';
    return null;
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
