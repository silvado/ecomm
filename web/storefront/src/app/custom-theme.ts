import { DOCUMENT } from '@angular/common';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, inject, Injectable, input, OnInit, RESPONSE_INIT } from '@angular/core';
import { ResolveFn } from '@angular/router';
import { catchError, of } from 'rxjs';
import { Seo } from './seo';
import { StoreContext } from './store';

/** Página exclusiva da loja personalizada (RF40 CA1). HTML já limpo no servidor ao salvar. */
export interface CustomPageContent {
  slug: string;
  title: string;
  html: string;
}

/** Carrinho, checkout e Meus pedidos ficam com o visual padrão (só as cores da loja): o tema exclusivo não mexe na venda. */
const PROTECTED_PATHS = ['/carrinho', '/pedido', '/meus-pedidos'];

export function isThemedPath(url: string): boolean {
  const path = url.split(/[?#]/, 1)[0];
  return !PROTECTED_PATHS.some((p) => path === p || path.startsWith(p + '/'));
}

const STYLE_ID = 'custom-theme';

/**
 * Coloca o CSS do tema num <style> do <head>. No SSR ele já sai no HTML; na hidratação o elemento é reaproveitado.
 * Defesa extra: o servidor já escapa "<", mas CSS com "<" nunca é aplicado (não fecha o <style>).
 */
@Injectable({ providedIn: 'root' })
export class CustomThemeStyle {
  private readonly document = inject(DOCUMENT);

  apply(css: string | null | undefined): void {
    const existing = this.document.getElementById(STYLE_ID);
    if (!css || css.includes('<')) {
      existing?.remove();
      return;
    }
    const style = existing ?? this.document.head.appendChild(this.document.createElement('style'));
    style.id = STYLE_ID;
    if (style.textContent !== css) style.textContent = css;
  }
}

export const customPageResolver: ResolveFn<CustomPageContent | null> = (route) => {
  const response = inject(RESPONSE_INIT, { optional: true });
  const slug = route.paramMap.get('slug') ?? '';
  const setStatus = (status: number) => {
    if (response) response.status = status;
  };
  if (!/^[a-z0-9-]{1,60}$/.test(slug)) {
    setStatus(404);
    return null;
  }
  return inject(HttpClient)
    .get<CustomPageContent>(`/api/loja/paginas/${slug}`)
    .pipe(
      catchError((error: unknown) => {
        setStatus(error instanceof HttpErrorResponse && error.status === 404 ? 404 : 503);
        return of(null);
      }),
    );
};

@Component({
  selector: 'app-custom-page',
  template: `
    @if (page(); as p) {
      <article class="custom-page" data-custom-page>
        <h1>{{ p.title }}</h1>
        <div [innerHTML]="p.html"></div>
      </article>
    } @else {
      <section>
        <h1>Página não encontrada</h1>
        <p>Confira o endereço ou volte para a página inicial.</p>
      </section>
    }
  `,
})
export class CustomPage implements OnInit {
  private readonly ctx = inject(StoreContext);
  private readonly seo = inject(Seo);

  /** Do resolver. */
  readonly page = input<CustomPageContent | null>(null);

  ngOnInit(): void {
    const name = this.ctx.store?.name ?? 'Loja';
    const page = this.page();
    if (page) this.seo.apply({ title: `${page.title} — ${name}`, description: `${page.title} — ${name}.`, path: `/p/${page.slug}`, origin: this.ctx.origin });
    else this.seo.apply({ title: `Página não encontrada — ${name}`, description: name, path: '/', origin: this.ctx.origin, noindex: true });
  }
}
