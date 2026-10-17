import { CurrencyPipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { Component, computed, inject, Injectable, input, OnInit, signal } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { problemTitle } from '../../core/auth';

export type ListingStatus = 'pending' | 'active' | 'pausedBySystem' | 'pausedByUser' | 'closed' | 'error';

export interface ListingView {
  id: string;
  channel: string;
  externalAccount: string;
  externalId: string | null;
  status: ListingStatus;
  lastPushedQuantity: number | null;
  lastPushedPrice: number | null;
  lastSyncedAt: string | null;
  lastError: string | null;
  lastReconciledAt?: string | null;
}

export const LISTING_STATUS: Record<ListingStatus, string> = {
  pending: 'Aguardando publicação',
  active: 'Ativo',
  pausedBySystem: 'Pausado (sem estoque)',
  pausedByUser: 'Pausado por você',
  closed: 'Encerrado',
  error: 'Erro na última atualização',
};

export const CHANNEL_NAMES: Record<string, string> = {
  'canal-simulado': 'Canal simulado (teste)',
  'mercado-livre': 'Mercado Livre',
  meta: 'Instagram / Facebook',
  olx: 'OLX',
};

/** Categoria do ML sugerida pelo título da peça. */
export interface CategoryOption {
  categoryId: string;
  categoryName: string;
  domainName: string | null;
}

/** Atributo da categoria do ML (só os que a loja preenche). */
export interface CategoryAttributeSpec {
  id: string;
  name: string;
  valueType: string;
  required: boolean;
  conditionalRequired: boolean;
  multivalued: boolean;
  maxLength: number | null;
  values: { id: string; name: string }[];
  units: string[];
  defaultUnit: string | null;
}

export interface AttributeValue {
  id: string;
  valueId: string | null;
  valueName: string | null;
}

export interface MercadoLivrePublishForm {
  category: { categoryId: string; name: string; path: string; maxTitleLength: number; maxPictures: number; listingAllowed: boolean };
  attributes: CategoryAttributeSpec[];
  prefilled: AttributeValue[];
  warnings: string[];
}

/** Compatibilidades enviadas ao anúncio do ML: aceitas, não achadas no catálogo de veículos do ML e erro. */
export interface CompatibilityReport {
  sent: number;
  unmapped: string[];
  error: string | null;
}

export interface MercadoLivreListingOptions {
  categoryId: string;
  listingTypeId: string;
  attributes: AttributeValue[];
  warrantyType: string | null;
  warrantyTime: string | null;
}

/** Anúncios da peça nos canais (E2): situação e pausa do lojista; publicação no Mercado Livre (RF21). */
@Injectable({ providedIn: 'root' })
export class ListingsApi {
  private readonly http = inject(HttpClient);

  forPart(partId: string): Promise<ListingView[]> {
    return firstValueFrom(this.http.get<ListingView[]>(`/api/painel/pecas/${partId}/anuncios`));
  }

  change(listingId: string, action: 'pausar' | 'retomar'): Promise<unknown> {
    return firstValueFrom(this.http.post(`/api/painel/anuncios/${listingId}/${action}`, {}));
  }

  mercadoLivreCategories(partId: string): Promise<CategoryOption[]> {
    return firstValueFrom(this.http.get<CategoryOption[]>(`/api/painel/pecas/${partId}/mercado-livre/categorias`));
  }

  mercadoLivreForm(partId: string, categoryId: string): Promise<MercadoLivrePublishForm> {
    return firstValueFrom(
      this.http.get<MercadoLivrePublishForm>(`/api/painel/pecas/${partId}/mercado-livre/formulario`, { params: { categoria: categoryId } }),
    );
  }

  publishMercadoLivre(partId: string, options: MercadoLivreListingOptions): Promise<{ listings: ListingView[]; compatibilities: CompatibilityReport | null }> {
    return firstValueFrom(
      this.http.post<{ listings: ListingView[]; compatibilities: CompatibilityReport | null }>(`/api/painel/pecas/${partId}/mercado-livre/anunciar`, options),
    );
  }

  sendCompatibilities(listingId: string): Promise<CompatibilityReport> {
    return firstValueFrom(this.http.post<CompatibilityReport>(`/api/painel/anuncios/${listingId}/compatibilidades`, {}));
  }
}

const WHEN = new Intl.DateTimeFormat('pt-BR', { timeZone: 'America/Sao_Paulo', dateStyle: 'short', timeStyle: 'short' });

/**
 * Onde a peça está anunciada. O estoque manda: zerou, o anúncio pausa sozinho; voltou, reativa. Pausado pelo lojista
 * fica pausado até ele retomar (RF24).
 */
@Component({
  selector: 'app-part-listings',
  imports: [CurrencyPipe, FormsModule, NgTemplateOutlet],
  template: `
    <section data-part-listings>
      <h2>Anúncios</h2>
      @if (listings().length) {
        <table>
          <thead><tr><th>Canal</th><th>Situação</th><th>Quantidade e preço no canal</th><th>Atualizado</th><th>Conferido</th><th></th></tr></thead>
          <tbody>
            @for (l of listings(); track l.id) {
              <tr [attr.data-listing]="l.status">
                <td>{{ channelName(l.channel) }}<br /><small class="muted">{{ l.externalId }}</small></td>
                <td>
                  {{ statusLabel[l.status] }}
                  @if (l.lastError) {
                    <br /><small class="error">{{ l.lastError }}</small>
                  }
                </td>
                <td>{{ l.lastPushedQuantity ?? '—' }} un. · {{ l.lastPushedPrice != null ? (l.lastPushedPrice | currency: 'BRL' : 'symbol' : '1.2-2' : 'pt-BR') : '—' }}</td>
                <td>{{ l.lastSyncedAt ? when(l.lastSyncedAt) : '—' }}</td>
                <td data-reconciled>{{ l.lastReconciledAt ? when(l.lastReconciledAt) : '—' }}</td>
                <td>
                  @if (l.status === 'active' || l.status === 'pausedBySystem' || l.status === 'error') {
                    <button type="button" class="link" (click)="change(l, 'pausar')" [disabled]="busy()" data-pause>Pausar</button>
                  } @else if (l.status === 'pausedByUser') {
                    <button type="button" class="link" (click)="change(l, 'retomar')" [disabled]="busy()" data-resume>Retomar</button>
                  }
                  @if (l.channel === 'mercado-livre' && l.externalId) {
                    <br /><button type="button" class="link" (click)="sendCompatibilities(l)" [disabled]="busy()" data-ml-compat>Enviar compatibilidades</button>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
        <small class="muted">Sem estoque, o anúncio pausa sozinho e volta quando houver saldo. Pausado por você só volta quando você retomar. De hora em hora conferimos o anúncio no canal e corrigimos o que estiver diferente do estoque.</small>
      } @else {
        <p class="muted">A peça ainda não está anunciada em nenhum canal.</p>
      }

      @if (compatibilityReport(); as r) {
        <div class="notice" role="status" data-compat-report>
          @if (r.sent > 0) {
            <p class="ok">Compatibilidades enviadas ao Mercado Livre: {{ r.sent }} veículo(s) ligados ao anúncio.</p>
          } @else if (!r.unmapped.length && !r.error) {
            <p class="muted">A peça não tem veículos em Compatibilidade: nada foi enviado ao Mercado Livre.</p>
          }
          @if (r.unmapped.length) {
            <p class="warn">
              Não achamos no catálogo de veículos do Mercado Livre (inclua pelo gerenciador de compatibilidades do Mercado Livre):
              {{ r.unmapped.join('; ') }}.
            </p>
          }
          @if (r.error) {
            <p class="error">{{ r.error }}</p>
          }
        </div>
      }

      @if (!hasMercadoLivre()) {
        @if (!publishing()) {
          <p><button type="button" (click)="startMercadoLivre()" [disabled]="busy()" data-ml-start>Anunciar no Mercado Livre</button></p>
        } @else {
          <fieldset class="ml-publish" data-ml-publish>
            <legend>Anunciar no Mercado Livre</legend>
            @if (categories().length) {
              <span class="label">Categoria (sugerida pelo título da peça)</span>
              <ul class="choices">
                @for (c of categories(); track c.categoryId) {
                  <li>
                    <label class="checkbox">
                      <input type="radio" name="ml-category" [value]="c.categoryId" [checked]="form()?.category?.categoryId === c.categoryId"
                        (change)="chooseCategory(c.categoryId)" [disabled]="busy()" />
                      {{ c.categoryName }}
                    </label>
                  </li>
                }
              </ul>
            } @else if (!busy()) {
              <p class="muted">O Mercado Livre não sugeriu categoria para este título. Ajuste o título da peça e tente de novo.</p>
            }

            @if (form(); as f) {
              <p class="muted">{{ f.category.path }}</p>
              @for (w of f.warnings; track w) {
                <p class="warn">{{ w }}</p>
              }
              @for (a of requiredAttributes(); track a.id) {
                <ng-container *ngTemplateOutlet="field; context: { $implicit: a }" />
              }
              @if (optionalAttributes().length) {
                <details>
                  <summary>Mais características (opcional, ajudam o anúncio a aparecer nas buscas)</summary>
                  @for (a of optionalAttributes(); track a.id) {
                    <ng-container *ngTemplateOutlet="field; context: { $implicit: a }" />
                  }
                </details>
              }
              <div class="grid-3">
                <label>
                  Tipo de anúncio
                  <select [(ngModel)]="listingType" name="ml-listing-type">
                    <option value="gold_special">Clássico</option>
                    <option value="gold_pro">Premium</option>
                  </select>
                </label>
                <label>
                  Garantia
                  <select [(ngModel)]="warrantyType" name="ml-warranty">
                    <option value="">Sem garantia</option>
                    <option value="Garantia do vendedor">Garantia do vendedor</option>
                    <option value="Garantia de fábrica">Garantia de fábrica</option>
                  </select>
                </label>
                @if (warrantyType) {
                  <label>Tempo de garantia <input type="text" [(ngModel)]="warrantyTime" name="ml-warranty-time" placeholder="90 dias" /></label>
                }
              </div>
              <small class="muted">O título do anúncio é o da peça (até {{ f.category.maxTitleLength }} caracteres); preço, quantidade e fotos também. O estoque da loja passa a mandar no anúncio.</small>
              <div class="order-actions">
                <button type="button" (click)="publish()" [disabled]="busy()" data-ml-publish-button>{{ busy() ? 'Publicando…' : 'Publicar no Mercado Livre' }}</button>
                <button type="button" class="link" (click)="cancel()" [disabled]="busy()">Cancelar</button>
              </div>
            }
            @if (publishError()) {
              <p class="error" role="alert">{{ publishError() }}</p>
            }
          </fieldset>
        }
      }

      <ng-template #field let-a>
        <label [attr.data-ml-attribute]="a.id">
          {{ a.name }}{{ a.required ? ' *' : '' }}
          @if (a.values.length) {
            <select [ngModel]="values[a.id]?.valueId ?? ''" (ngModelChange)="setValue(a.id, $event, null)" [name]="'ml-' + a.id">
              <option value="">—</option>
              @for (v of a.values; track v.id) {
                <option [value]="v.id">{{ v.name }}</option>
              }
            </select>
          } @else {
            <input type="text" [ngModel]="values[a.id]?.valueName ?? ''" (ngModelChange)="setValue(a.id, null, $event)" [name]="'ml-' + a.id"
              [attr.maxlength]="a.maxLength" [placeholder]="a.valueType === 'number_unit' ? 'ex.: 10 ' + (a.defaultUnit ?? '') : ''" />
          }
          @if (a.conditionalRequired) {
            <small>Pode ser obrigatório conforme a peça (o Mercado Livre avisa ao publicar).</small>
          }
        </label>
      </ng-template>

      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
    </section>
  `,
})
export class PartListings implements OnInit {
  readonly partId = input.required<string>();

  private readonly api = inject(ListingsApi);

  protected readonly listings = signal<ListingView[]>([]);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly statusLabel = LISTING_STATUS;
  protected readonly when = (iso: string) => WHEN.format(new Date(iso));
  protected readonly channelName = (c: string) => CHANNEL_NAMES[c] ?? c;

  // Publicação no Mercado Livre (RF21 CA3).
  protected readonly hasMercadoLivre = computed(() => this.listings().some((l) => l.channel === 'mercado-livre'));
  protected readonly publishing = signal(false);
  protected readonly categories = signal<CategoryOption[]>([]);
  protected readonly form = signal<MercadoLivrePublishForm | null>(null);
  protected readonly publishError = signal<string | null>(null);
  protected readonly compatibilityReport = signal<CompatibilityReport | null>(null);
  protected readonly requiredAttributes = computed(() => (this.form()?.attributes ?? []).filter((a) => a.required));
  protected readonly optionalAttributes = computed(() => (this.form()?.attributes ?? []).filter((a) => !a.required));
  protected values: Record<string, { valueId: string | null; valueName: string | null }> = {};
  protected listingType = 'gold_special';
  protected warrantyType = '';
  protected warrantyTime = '90 dias';

  async ngOnInit(): Promise<void> {
    await this.load();
  }

  protected async change(listing: ListingView, action: 'pausar' | 'retomar'): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await this.api.change(listing.id, action);
      await this.load();
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }

  protected async startMercadoLivre(): Promise<void> {
    this.publishing.set(true);
    this.publishError.set(null);
    await this.run(async () => {
      const categories = await this.api.mercadoLivreCategories(this.partId());
      this.categories.set(categories);
      if (categories.length) await this.loadForm(categories[0].categoryId);
    });
  }

  protected async chooseCategory(categoryId: string): Promise<void> {
    await this.run(() => this.loadForm(categoryId));
  }

  protected setValue(id: string, valueId: string | null, valueName: string | null): void {
    this.values = { ...this.values, [id]: { valueId: valueId || null, valueName: valueName || null } };
  }

  protected cancel(): void {
    this.publishing.set(false);
    this.form.set(null);
    this.categories.set([]);
    this.publishError.set(null);
  }

  protected async publish(): Promise<void> {
    const f = this.form();
    if (!f) return;
    await this.run(async () => {
      const published = await this.api.publishMercadoLivre(this.partId(), {
          categoryId: f.category.categoryId,
          listingTypeId: this.listingType,
          attributes: Object.entries(this.values)
            .filter(([, v]) => v.valueId || v.valueName)
            .map(([id, v]) => ({ id, valueId: v.valueId, valueName: v.valueName })),
          warrantyType: this.warrantyType || null,
          warrantyTime: this.warrantyType ? this.warrantyTime || null : null,
        });
      this.listings.set(published.listings);
      this.compatibilityReport.set(published.compatibilities);
      this.cancel();
    });
  }

  protected async sendCompatibilities(listing: ListingView): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      this.compatibilityReport.set(await this.api.sendCompatibilities(listing.id));
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }

  private async loadForm(categoryId: string): Promise<void> {
    const form = await this.api.mercadoLivreForm(this.partId(), categoryId);
    this.values = Object.fromEntries(form.prefilled.map((p) => [p.id, { valueId: p.valueId, valueName: p.valueName }]));
    this.form.set(form);
  }

  private async run(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.publishError.set(null);
    try {
      await action();
    } catch (error) {
      this.publishError.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }

  private async load(): Promise<void> {
    try {
      this.listings.set(await this.api.forPart(this.partId()));
    } catch (error) {
      this.error.set(problemTitle(error));
    }
  }
}