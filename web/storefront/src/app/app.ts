import { Component, inject } from '@angular/core';
import { RouterLink, RouterOutlet } from '@angular/router';
import { CartStore } from './cart';
import { StoreContext } from './store';

/**
 * Moldura da loja: o tema do tenant vira CSS custom properties no elemento raiz (ADR-0006) — sem build por cliente.
 * Loja inexistente ou indisponível mostra a página correspondente (RF04 CA2/CA3) com o status HTTP certo.
 */
@Component({
  imports: [RouterOutlet, RouterLink],
  selector: 'app-root',
  template: `
    @switch (ctx.state.kind) {
      @case ('open') {
        <div
          class="store"
          [style.--color-primary]="ctx.store!.theme.primaryColor"
          [style.--color-on-primary]="ctx.store!.theme.onPrimaryColor"
          [style.--color-background]="ctx.store!.theme.backgroundColor"
          [style.--color-text]="ctx.store!.theme.textColor"
        >
          <header>
            <a routerLink="/" class="brand">
              @if (ctx.store!.logoUrl) {
                <img [src]="ctx.store!.logoUrl" [alt]="ctx.store!.name" height="48" />
              } @else {
                {{ ctx.store!.name }}
              }
            </a>
            <nav>
              <a routerLink="/busca">Buscar peças</a>
              <a routerLink="/carrinho" class="cart-link">Carrinho @if (cart.count()) { <span class="badge">{{ cart.count() }}</span> }</a>
            </nav>
          </header>
          <main><router-outlet /></main>
          <footer>
            @if (ctx.store!.texts.footer) {
              <p class="pre">{{ ctx.store!.texts.footer }}</p>
            }
            @if (ctx.store!.texts.returnPolicy) {
              <a routerLink="/trocas-e-devolucoes">Trocas e devoluções</a>
            }
          </footer>
        </div>
      }
      @case ('unavailable') {
        <main class="notice">
          <h1>Loja indisponível no momento</h1>
          <p>Volte mais tarde.</p>
        </main>
      }
      @case ('not-found') {
        <main class="notice">
          <h1>Loja não encontrada</h1>
          <p>Confira o endereço digitado.</p>
        </main>
      }
      @default {
        <main class="notice">
          <h1>Não foi possível abrir a loja</h1>
          <p>Tente novamente em instantes.</p>
        </main>
      }
    }
  `,
})
export class App {
  protected readonly ctx = inject(StoreContext);
  protected readonly cart = inject(CartStore);
}
