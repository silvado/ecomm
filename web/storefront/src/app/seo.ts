import { DOCUMENT } from '@angular/common';
import { inject, Injectable } from '@angular/core';
import { Meta, Title } from '@angular/platform-browser';

export interface PageSeo {
  title: string;
  description: string;
  /** Caminho canônico (começa com "/"); a origem vem da loja. */
  path: string;
  origin: string;
  image?: string;
  type?: 'website' | 'product';
  /** Dados estruturados (schema.org) — ex.: Product na página da peça. */
  jsonLd?: object;
  /** Páginas pessoais (carrinho, checkout) não entram no Google. */
  noindex?: boolean;
}

const JSON_LD_ID = 'jsonld';

/**
 * Título, descrição, canonical, Open Graph e JSON-LD da página (RF13 CA2). Funciona no SSR (robôs e prévias de
 * redes sociais leem o HTML do servidor) e é atualizado na navegação do navegador.
 */
@Injectable({ providedIn: 'root' })
export class Seo {
  private readonly title = inject(Title);
  private readonly meta = inject(Meta);
  private readonly document = inject(DOCUMENT);

  apply(page: PageSeo): void {
    const url = page.origin + page.path;
    this.title.setTitle(page.title);
    this.meta.updateTag({ name: 'description', content: page.description });
    this.meta.updateTag({ property: 'og:title', content: page.title });
    this.meta.updateTag({ property: 'og:description', content: page.description });
    this.meta.updateTag({ property: 'og:type', content: page.type ?? 'website' });
    this.meta.updateTag({ property: 'og:url', content: url });
    this.meta.updateTag({ property: 'og:locale', content: 'pt_BR' });
    if (page.image) this.meta.updateTag({ property: 'og:image', content: page.image });
    else this.meta.removeTag("property='og:image'");
    if (page.noindex) this.meta.updateTag({ name: 'robots', content: 'noindex' });
    else this.meta.removeTag("name='robots'");
    this.setCanonical(url);
    this.setJsonLd(page.jsonLd);
  }

  private setCanonical(url: string): void {
    let link = this.document.head.querySelector<HTMLLinkElement>('link[rel="canonical"]');
    if (!link) {
      link = this.document.createElement('link');
      link.rel = 'canonical';
      this.document.head.appendChild(link);
    }
    link.href = url;
  }

  private setJsonLd(data: object | undefined): void {
    this.document.getElementById(JSON_LD_ID)?.remove();
    if (!data) return;
    const script = this.document.createElement('script');
    script.id = JSON_LD_ID;
    script.type = 'application/ld+json';
    // "<" escapado: texto do lojista (título, descrição) não consegue fechar o <script> e injetar HTML.
    script.textContent = JSON.stringify(data).replace(/</g, '\\u003c');
    this.document.head.appendChild(script);
  }
}
