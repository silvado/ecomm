import { CurrencyPipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { problemTitle } from '../../core/auth';
import { CounterPaymentMethod, OrdersApi, OrderView, PAYMENT_METHOD_LABELS } from '../orders/orders.api';
import { PartsApi, PartSummary } from '../parts/parts.api';

interface Line {
  part: PartSummary;
  quantity: number;
  unitPrice: number;
}

/** 32 bytes em base64url: identifica esta venda; reenviar o mesmo formulário não lança outra (RF18). */
export function newSaleToken(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(32));
  return btoa(String.fromCharCode(...bytes)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

/**
 * Venda de balcão (RF18): busca a peça, ajusta quantidade e preço (desconto negociado), escolhe a forma de pagamento e,
 * se quiser, informa o cliente. A venda já sai paga e retirada, com baixa no estoque na hora.
 */
@Component({
  selector: 'app-counter',
  imports: [FormsModule, RouterLink, CurrencyPipe],
  template: `
    <main class="card wide">
      <h1>Venda de balcão</h1>

      @if (done(); as d) {
        <p class="ok" role="status">
          Venda nº {{ d.number }} registrada: {{ d.total | currency: 'BRL' : 'symbol' : '1.2-2' : 'pt-BR' }}.
          <a [routerLink]="['/pedidos']" [queryParams]="{ busca: d.number }">Ver no painel de pedidos</a>
        </p>
      }

      <section>
        <h2>Peças</h2>
        <div class="filters">
          <input type="search" placeholder="Código interno, título ou OEM" [(ngModel)]="search" (ngModelChange)="searchChanged()" aria-label="Buscar peça" />
        </div>
        @if (results().length) {
          <table data-results>
            <tbody>
              @for (p of results(); track p.id) {
                <tr>
                  <td>{{ p.internalCode }}</td>
                  <td>{{ p.title }}</td>
                  <td class="num">{{ p.price | currency: 'BRL' : 'symbol' : '1.2-2' : 'pt-BR' }}</td>
                  <td class="num">{{ p.stock.available }} disp.</td>
                  <td class="actions">
                    <button type="button" class="link" (click)="add(p)" [disabled]="p.stock.available < 1 || inCart(p.id)" [attr.data-add]="p.id">
                      {{ inCart(p.id) ? 'Na venda' : p.stock.available < 1 ? 'Sem estoque' : 'Adicionar' }}
                    </button>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        } @else if (search.trim() && !searching()) {
          <p class="muted">Nenhuma peça encontrada.</p>
        }
      </section>

      <form (ngSubmit)="sell()" #f="ngForm">
        @if (lines().length) {
          <table data-lines>
            <thead>
              <tr>
                <th>Peça</th>
                <th class="num">Qtd.</th>
                <th class="num">Preço (R$)</th>
                <th class="num">Subtotal</th>
                <th><span class="sr-only">Ações</span></th>
              </tr>
            </thead>
            <tbody>
              @for (line of lines(); track line.part.id; let i = $index) {
                <tr>
                  <td>
                    {{ line.part.internalCode }} · {{ line.part.title }}
                    @if (line.unitPrice !== line.part.price) {
                      <br /><small class="muted">Tabela: {{ line.part.price | currency: 'BRL' : 'symbol' : '1.2-2' : 'pt-BR' }}</small>
                    }
                  </td>
                  <td class="num">
                    <input type="number" [name]="'qtd' + i" min="1" [max]="line.part.stock.available" step="1" required [ngModel]="line.quantity"
                      (ngModelChange)="update(i, { quantity: $event })" [attr.aria-label]="'Quantidade de ' + line.part.internalCode" />
                  </td>
                  <td class="num">
                    <input type="number" [name]="'preco' + i" min="0.01" step="0.01" required [ngModel]="line.unitPrice"
                      (ngModelChange)="update(i, { unitPrice: $event })" [attr.aria-label]="'Preço de ' + line.part.internalCode" />
                  </td>
                  <td class="num">{{ line.quantity * line.unitPrice | currency: 'BRL' : 'symbol' : '1.2-2' : 'pt-BR' }}</td>
                  <td class="actions"><button type="button" class="link danger" (click)="remove(i)">Remover</button></td>
                </tr>
              }
              <tr>
                <td colspan="3"><strong>Total</strong></td>
                <td class="num"><strong data-total>{{ total() | currency: 'BRL' : 'symbol' : '1.2-2' : 'pt-BR' }}</strong></td>
                <td></td>
              </tr>
            </tbody>
          </table>
        } @else {
          <p class="muted">Busque e adicione as peças vendidas.</p>
        }

        <fieldset>
          <legend>Pagamento</legend>
          <select name="paymentMethod" [(ngModel)]="paymentMethod" required aria-label="Forma de pagamento">
            <option value="" disabled>Escolha a forma de pagamento</option>
            @for (m of methods; track m) {
              <option [value]="m">{{ methodLabels[m] }}</option>
            }
          </select>
        </fieldset>

        <fieldset>
          <legend>Cliente (opcional)</legend>
          <div class="grid-3">
            <label>Nome <input name="name" maxlength="120" [(ngModel)]="customer.name" /></label>
            <label>CPF ou CNPJ <input name="document" maxlength="18" [(ngModel)]="customer.document" /></label>
            <label>Telefone com DDD <input name="phone" type="tel" maxlength="16" [(ngModel)]="customer.phone" /></label>
          </div>
          <small>Sem nome, a venda fica como "Consumidor final".</small>
        </fieldset>

        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }
        <button type="submit" [disabled]="!lines().length || !paymentMethod || f.invalid || busy()">
          {{ busy() ? 'Registrando…' : 'Registrar venda' }}
        </button>
      </form>
    </main>
  `,
})
export class CounterPage {
  private readonly parts = inject(PartsApi);
  private readonly orders = inject(OrdersApi);
  private debounce?: ReturnType<typeof setTimeout>;
  private token = newSaleToken();

  protected readonly methods: CounterPaymentMethod[] = ['cash', 'pix', 'debitCard', 'creditCard', 'other'];
  protected readonly methodLabels = PAYMENT_METHOD_LABELS;
  protected search = '';
  protected paymentMethod: CounterPaymentMethod | '' = '';
  protected customer = { name: '', document: '', phone: '' };
  protected readonly results = signal<PartSummary[]>([]);
  protected readonly lines = signal<Line[]>([]);
  protected readonly searching = signal(false);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly done = signal<OrderView | null>(null);
  protected readonly total = computed(() => this.lines().reduce((sum, l) => sum + l.quantity * l.unitPrice, 0));
  protected readonly inCart = (id: string) => this.lines().some((l) => l.part.id === id);

  protected searchChanged(): void {
    clearTimeout(this.debounce);
    this.debounce = setTimeout(() => void this.find(), 300);
  }

  protected add(part: PartSummary): void {
    if (this.inCart(part.id) || part.stock.available < 1) return;
    this.done.set(null);
    this.lines.update((lines) => [...lines, { part, quantity: 1, unitPrice: part.price }]);
  }

  protected update(index: number, change: Partial<Pick<Line, 'quantity' | 'unitPrice'>>): void {
    this.lines.update((lines) => lines.map((l, i) => (i === index ? { ...l, ...change } : l)));
  }

  protected remove(index: number): void {
    this.lines.update((lines) => lines.filter((_, i) => i !== index));
  }

  protected async sell(): Promise<void> {
    if (!this.lines().length || !this.paymentMethod) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const sale = await this.orders.sell({
        token: this.token,
        items: this.lines().map((l) => ({ partId: l.part.id, quantity: l.quantity, unitPrice: l.unitPrice })),
        paymentMethod: this.paymentMethod,
        customer: { name: this.customer.name.trim(), document: this.customer.document.trim(), phone: this.customer.phone.trim() },
      });
      this.done.set(sale);
      this.token = newSaleToken();
      this.lines.set([]);
      this.paymentMethod = '';
      this.customer = { name: '', document: '', phone: '' };
      await this.find();
    } catch (error) {
      this.error.set(problemTitle(error));
      // Saldo mudou (outra venda): mostra o disponível atual.
      await this.find();
    } finally {
      this.busy.set(false);
    }
  }

  private async find(): Promise<void> {
    const text = this.search.trim();
    if (!text) {
      this.results.set([]);
      return;
    }
    this.searching.set(true);
    try {
      this.results.set((await this.parts.search(text, '', 1, 10)).items);
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.searching.set(false);
    }
  }
}
