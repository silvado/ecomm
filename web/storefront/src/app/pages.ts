import { CurrencyPipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { CartStore } from './cart';
import {
  CONDITION_LABELS,
  CONDITION_SCHEMA,
  partPath,
  photoSrcset,
  photoUrl,
  StorePartPage,
  StorePartView,
  summarize,
} from './catalog';
import { PartCard } from './part-card';
import { searchParamsFrom } from './resolvers';
import { Seo } from './seo';
import { StoreContext } from './store';
import { VehicleFinder } from './vehicle-finder';

@Component({
  selector: 'app-home',
  imports: [VehicleFinder, PartCard],
  template: `
    <section class="hero">
      <h1>{{ ctx.store?.name }}</h1>
      @if (ctx.store?.texts?.about) {
        <p class="pre about">{{ ctx.store?.texts?.about }}</p>
      }
    </section>
    <app-vehicle-finder />
    <h2>Peças recentes</h2>
    @if (recent().items.length) {
      <div class="grid">
        @for (part of recent().items; track part.id) {
          <app-part-card [part]="part" />
        }
      </div>
    } @else {
      <p class="muted">Nenhuma peça à venda no momento.</p>
    }
  `,
})
export class HomePage {
  readonly recent = input.required<StorePartPage>();
  protected readonly ctx = inject(StoreContext);

  constructor() {
    const seo = inject(Seo);
    const store = this.ctx.store;
    if (store) {
      seo.apply({
        title: store.name,
        description: summarize(store.texts.about || `Peças automotivas na ${store.name}.`),
        path: '/',
        origin: this.ctx.origin,
      });
    }
  }
}

@Component({
  selector: 'app-search',
  imports: [VehicleFinder, PartCard, RouterLink],
  template: `
    <app-vehicle-finder [initial]="params()" />
    <p class="muted results">{{ result().total }} {{ result().total === 1 ? 'peça encontrada' : 'peças encontradas' }}</p>
    @if (result().items.length) {
      <div class="grid">
        @for (part of result().items; track part.id) {
          <app-part-card [part]="part" />
        }
      </div>
    } @else {
      <p>Nenhuma peça encontrada. Tente outro termo ou outro veículo.</p>
    }
    @if (pages() > 1) {
      <nav class="pager" aria-label="Páginas">
        @if (result().page > 1) {
          <a [routerLink]="[]" [queryParams]="{ pagina: result().page - 1 }" queryParamsHandling="merge" rel="prev">Anterior</a>
        }
        <span>Página {{ result().page }} de {{ pages() }}</span>
        @if (result().page < pages()) {
          <a [routerLink]="[]" [queryParams]="{ pagina: result().page + 1 }" queryParamsHandling="merge" rel="next">Próxima</a>
        }
      </nav>
    }
  `,
})
export class SearchPage {
  readonly result = input.required<StorePartPage>();

  private readonly route = inject(ActivatedRoute);
  private readonly query = signal(searchParamsFrom(this.route.snapshot.queryParams));
  protected readonly params = this.query.asReadonly();
  protected readonly pages = computed(() => Math.max(1, Math.ceil(this.result().total / this.result().pageSize)));

  constructor() {
    const seo = inject(Seo);
    const ctx = inject(StoreContext);
    this.route.queryParams.subscribe((query) => this.query.set(searchParamsFrom(query)));
    effect(() => {
      const q = this.params().q?.trim();
      const name = ctx.store?.name ?? 'Loja';
      seo.apply({
        title: q ? `${q} — ${name}` : `Buscar peças — ${name}`,
        description: `Peças automotivas na ${name}: busque por nome, código, OEM ou pelo seu veículo.`,
        path: '/busca',
        origin: ctx.origin,
      });
    });
  }
}

@Component({
  selector: 'app-part',
  imports: [CurrencyPipe, RouterLink, FormsModule],
  template: `
    @if (part(); as p) {
      <article class="part">
        <div class="gallery">
          @if (photo(); as current) {
            <img
              class="main-photo"
              [src]="large(current)"
              [attr.srcset]="srcset(current)"
              sizes="(max-width: 800px) 100vw, 640px"
              [alt]="p.title"
              width="800"
              height="800"
              fetchpriority="high"
            />
          }
          @if (p.photoIds.length > 1) {
            <ul class="thumbs">
              @for (id of p.photoIds; track id; let i = $index) {
                <li>
                  <button type="button" (click)="selected.set(i)" [attr.aria-label]="'Foto ' + (i + 1)" [class.active]="selected() === i">
                    <img [src]="small(id)" alt="" width="72" height="72" loading="lazy" />
                  </button>
                </li>
              }
            </ul>
          }
        </div>
        <div class="info">
          <h1>{{ p.title }}</h1>
          <p class="muted">{{ conditionLabels[p.condition] }} · Código {{ p.internalCode }}</p>
          @if (p.available > 0) {
            <p class="price">{{ p.price | currency: 'BRL' : 'symbol' : '1.2-2' }}</p>
            <p class="stock">{{ p.available === 1 ? 'Última unidade' : p.available + ' disponíveis' }}</p>
            <div class="buy">
              @if (p.available > 1) {
                <label>
                  <span class="sr-only">Quantidade</span>
                  <select [(ngModel)]="quantity">
                    @for (n of quantities(p.available); track n) {
                      <option [ngValue]="n">{{ n }}</option>
                    }
                  </select>
                </label>
              }
              <button type="button" (click)="addToCart(p.id)">Adicionar ao carrinho</button>
            </div>
            @if (added()) {
              <p class="added" role="status">Adicionada. <a routerLink="/carrinho">Ver carrinho</a></p>
            }
          } @else {
            <p class="unavailable">Indisponível no momento</p>
          }
          @if (p.oemCodes.length) {
            <p><strong>OEM:</strong> {{ p.oemCodes.join(', ') }}</p>
          }
          @if (p.description) {
            <h2>Descrição</h2>
            <p class="pre">{{ p.description }}</p>
          }
          @if (p.compatibilities.length) {
            <h2>Serve em</h2>
            <table class="compat">
              <thead>
                <tr><th>Veículo</th><th>Motorização</th><th>Anos</th></tr>
              </thead>
              <tbody>
                @for (c of p.compatibilities; track $index) {
                  <tr>
                    <td>{{ c.brand }} {{ c.model }}</td>
                    <td>{{ c.engine }}</td>
                    <td>{{ c.yearFrom }}–{{ c.yearTo ?? 'atual' }}</td>
                  </tr>
                }
              </tbody>
            </table>
          }
        </div>
      </article>
    } @else {
      <section class="notice-inline">
        <h1>Peça não encontrada</h1>
        <p>Ela pode ter sido vendida ou retirada da loja. <a routerLink="/busca">Buscar outras peças</a></p>
      </section>
    }
  `,
})
export class PartPage {
  readonly part = input<StorePartView | null>(null);

  protected readonly conditionLabels = CONDITION_LABELS;
  protected readonly selected = signal(0);
  protected readonly photo = computed(() => this.part()?.photoIds[this.selected()] ?? null);
  protected readonly large = (id: string) => photoUrl(id, 800);
  protected readonly small = (id: string) => photoUrl(id, 300);
  protected readonly srcset = photoSrcset;
  protected readonly added = signal(false);
  protected quantity = 1;
  private readonly cart = inject(CartStore);

  protected quantities(available: number): number[] {
    return Array.from({ length: Math.min(available, 20) }, (_, i) => i + 1);
  }

  protected addToCart(partId: string): void {
    this.cart.add(partId, this.quantity);
    this.added.set(true);
  }

  constructor() {
    const seo = inject(Seo);
    const ctx = inject(StoreContext);
    effect(() => {
      this.selected.set(0);
      this.added.set(false);
      this.quantity = 1;
      const part = this.part();
      const storeName = ctx.store?.name ?? 'Loja';
      if (!part) {
        seo.apply({ title: `Peça não encontrada — ${storeName}`, description: 'Peça não encontrada.', path: '/busca', origin: ctx.origin });
        return;
      }
      const path = partPath(part);
      const images = part.photoIds.map((id) => ctx.origin + photoUrl(id, 1600));
      seo.apply({
        title: `${part.title} — ${storeName}`,
        description: summarize(part.description || `${part.title} (${CONDITION_LABELS[part.condition].toLowerCase()}) na ${storeName}.`),
        path,
        origin: ctx.origin,
        type: 'product',
        image: images[0],
        // RF13 CA2: dados estruturados de produto (https://schema.org/Product, https://schema.org/Offer).
        jsonLd: {
          '@context': 'https://schema.org',
          '@type': 'Product',
          name: part.title,
          description: part.description || undefined,
          sku: part.internalCode,
          mpn: part.oemCodes[0],
          image: images,
          itemCondition: CONDITION_SCHEMA[part.condition],
          offers: {
            '@type': 'Offer',
            url: ctx.origin + path,
            priceCurrency: 'BRL',
            price: part.price.toFixed(2),
            availability: part.available > 0 ? 'https://schema.org/InStock' : 'https://schema.org/OutOfStock',
            itemCondition: CONDITION_SCHEMA[part.condition],
            seller: { '@type': 'Organization', name: storeName },
          },
        },
      });
    });
  }
}

@Component({
  selector: 'app-return-policy',
  template: `
    <section>
      <h1>Trocas e devoluções</h1>
      <p class="pre">{{ ctx.store?.texts?.returnPolicy || 'Consulte a loja.' }}</p>
    </section>
  `,
})
export class ReturnPolicyPage {
  protected readonly ctx = inject(StoreContext);

  constructor() {
    const name = this.ctx.store?.name ?? 'Loja';
    inject(Seo).apply({ title: `Trocas e devoluções — ${name}`, description: `Política de trocas e devoluções da ${name}.`, path: '/trocas-e-devolucoes', origin: this.ctx.origin });
  }
}
