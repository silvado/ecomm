import { HttpClient } from '@angular/common/http';
import { Component, computed, inject, input, OnInit } from '@angular/core';
import { ResolveFn } from '@angular/router';
import { catchError, of } from 'rxjs';
import { Seo } from './seo';
import { StoreContext } from './store';

/** Política de privacidade e termos da loja (RNF04): os textos dela ou o modelo padrão da plataforma. */
export interface StoreLegalTexts {
  privacyPolicy: string;
  terms: string;
}

export const legalTextsResolver: ResolveFn<StoreLegalTexts | null> = () =>
  inject(HttpClient).get<StoreLegalTexts>('/api/loja/privacidade').pipe(catchError(() => of(null)));

export const LEGAL_PAGES = {
  privacy: { title: 'Política de privacidade', path: '/privacidade', field: 'privacyPolicy' },
  terms: { title: 'Termos de uso', path: '/termos', field: 'terms' },
} as const;

export type LegalKind = keyof typeof LEGAL_PAGES;

@Component({
  selector: 'app-legal-page',
  template: `
    <section>
      <h1>{{ page().title }}</h1>
      @if (texts(); as t) {
        <p class="pre" data-legal-text>{{ t[page().field] }}</p>
      } @else {
        <p>Não foi possível carregar o texto agora. Tente novamente em instantes.</p>
      }
    </section>
  `,
})
export class LegalPage implements OnInit {
  private readonly ctx = inject(StoreContext);
  private readonly seo = inject(Seo);

  /** Da rota (data: { kind }). */
  readonly kind = input.required<LegalKind>();
  /** Do resolver. */
  readonly texts = input<StoreLegalTexts | null>(null);

  protected readonly page = computed(() => LEGAL_PAGES[this.kind()]);

  ngOnInit(): void {
    const name = this.ctx.store?.name ?? 'Loja';
    const page = this.page();
    this.seo.apply({ title: `${page.title} — ${name}`, description: `${page.title} da ${name}.`, path: page.path, origin: this.ctx.origin });
  }
}
