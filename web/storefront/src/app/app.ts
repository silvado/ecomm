import { Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterOutlet } from '@angular/router';
import { filter, map } from 'rxjs';
import { CartStore } from './cart';
import { CustomThemeStyle, isThemedPath } from './custom-theme';
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
          @if (custom?.headerHtml) {
            <div class="custom-header custom-theme" data-custom-header [innerHTML]="custom!.headerHtml"></div>
          }
          <header [class.custom-theme]="!!custom">
            <a routerLink="/" class="brand">
              @if (ctx.store!.logoUrl) {
                <img [src]="ctx.store!.logoUrl" [alt]="ctx.store!.name" height="48" />
              } @else {
                {{ ctx.store!.name }}
              }
            </a>
            <nav>
              <a routerLink="/busca">Buscar peças</a>
              <a routerLink="/meus-pedidos">Meus pedidos</a>
              <a routerLink="/carrinho" class="cart-link">Carrinho @if (cart.count()) { <span class="badge">{{ cart.count() }}</span> }</a>
            </nav>
          </header>
          <main [class.custom-theme]="!!custom && themed()"><router-outlet /></main>
          <footer [class.custom-theme]="!!custom">
            @if (custom?.footerHtml) {
              <div class="custom-footer" data-custom-footer [innerHTML]="custom!.footerHtml"></div>
            } @else if (ctx.store!.texts.footer) {
              <p class="pre">{{ ctx.store!.texts.footer }}</p>
            }
            @for (page of custom?.pages ?? []; track page.slug) {
              <a [routerLink]="['/p', page.slug]">{{ page.title }}</a>
            }
            @if (ctx.store!.texts.returnPolicy) {
              <a routerLink="/trocas-e-devolucoes">Trocas e devoluções</a>
            }
            <a routerLink="/termos">Termos de uso</a>
            <a routerLink="/privacidade">Política de privacidade</a>
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
  /** Tema exclusivo da loja personalizada (RF40 CA1); nulo no visual padrão. */
  protected readonly custom = this.ctx.store?.custom ?? null;

  private readonly router = inject(Router);
  private readonly url = toSignal(this.router.events.pipe(filter((e) => e instanceof NavigationEnd), map(() => this.router.url)), {
    initialValue: this.router.url,
  });
  /** Fora do carrinho, do checkout e de Meus pedidos. */
  protected readonly themed = computed(() => isThemedPath(this.url()));

  constructor() {
    inject(CustomThemeStyle).apply(this.custom?.css);
  }
}
