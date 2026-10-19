import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Observable } from 'rxjs';
import { problemTitle } from '../../core/auth';
import {
  CONDITION_LABELS,
  MAX_SUGGESTION_NOTES,
  MAX_SUGGESTION_PHOTOS,
  parseOemCodes,
  PartCondition,
  PartRequest,
  PartsApi,
  PartView,
  STATUS_LABELS,
  SuggestedCompatibility,
} from './parts.api';
import { VehiclesApi, yearRange } from './vehicles.api';
import { Compatibilities } from './compatibilities';
import { IntegrationLog } from '../integrations/integration-log';
import { PartTraceability } from './part-traceability';
import { PartFiscal } from '../fiscal/part-fiscal';
import { PartListings } from './part-listings';
import { PhotoThumb } from './photo-thumb';

/** Mesmos limites do domínio (Part). */
/** Mesmos limites da API (Part.MaxPhotos, PartPhoto.MaxUploadBytes). */
const MAX_PHOTOS = 20;
const MAX_PHOTO_BYTES = 10 * 1024 * 1024;

const LIMITS = { code: 60, title: 120, description: 5000, dimensionCm: 1000, weightG: 1_000_000, quantity: 1_000_000 };

/** Campos que a IA pode preencher (RF30 CA3: destacados até a pessoa revisar). */
type AiField = 'title' | 'description' | 'condition' | 'oemCodes';
const AI_FIELDS: AiField[] = ['title', 'description', 'condition', 'oemCodes'];

@Component({
  selector: 'app-part-form',
  imports: [ReactiveFormsModule, RouterLink, PhotoThumb, Compatibilities, PartTraceability, PartFiscal, PartListings, IntegrationLog],
  template: `
    <main class="card">
      <p><a routerLink="/pecas">← Peças</a></p>
      <h1>{{ part() ? part()!.internalCode + ' · ' + part()!.title : 'Nova peça' }}</h1>

      @if (part(); as p) {
        <div class="status-row">
          <span class="badge" [class.ok]="p.status === 'active'">{{ statusLabels[p.status] }}</span>
          @if (p.status !== 'active') {
            <button type="button" class="link" (click)="setStatus('active')" [disabled]="busy()">Ativar</button>
          }
          @if (p.status === 'active') {
            <button type="button" class="link danger" (click)="setStatus('inactive')" [disabled]="busy()">Inativar</button>
          }
          <span class="muted">
            Estoque: {{ p.stock.available }} disponível · {{ p.stock.onHand }} físico · {{ p.stock.reserved }} reservado
          </span>
        </div>
      }

      @if (part(); as p) {
        <section class="photos">
          <h2>Fotos ({{ p.photos.length }}/{{ maxPhotos }})</h2>
          @if (p.photos.length === 0) {
            <p class="muted">A peça precisa de ao menos uma foto para ser ativada.</p>
          }
          <ol class="gallery">
            @for (photo of sortedPhotos(); track photo.id; let first = $first; let last = $last) {
              <li>
                <app-photo-thumb [partId]="p.id" [photoId]="photo.id" [size]="120" [alt]="'Foto ' + (photo.position + 1)" />
                @if (first) {
                  <span class="badge">Capa</span>
                }
                <div class="photo-actions">
                  <button type="button" class="link" (click)="move(photo.id, -1)" [disabled]="first || busy()" aria-label="Mover para a esquerda">←</button>
                  <button type="button" class="link danger" (click)="removePhoto(photo.id)" [disabled]="busy()">Remover</button>
                  <button type="button" class="link" (click)="move(photo.id, 1)" [disabled]="last || busy()" aria-label="Mover para a direita">→</button>
                </div>
              </li>
            }
          </ol>
          @if (p.photos.length < maxPhotos) {
            <label class="button-like">
              {{ uploading() ? 'Enviando ' + uploading() + '…' : 'Adicionar fotos' }}
              <input type="file" accept="image/jpeg,image/png,image/webp" multiple (change)="addPhotos($event)" [disabled]="busy()" hidden />
            </label>
            <small>JPG, PNG ou WebP, até 10 MB cada. A primeira foto é a capa.</small>
          }
          @if (photoError()) {
            <p class="error" role="alert">{{ photoError() }}</p>
          }
        </section>

        <app-compatibilities [partId]="p.id" [compatibilities]="p.compatibilities" (changed)="part.set($event)" />

        <app-part-traceability [part]="p" (changed)="part.set($event)" />

        @defer (on viewport) {
          <app-part-fiscal [part]="p" (changed)="part.set($event)" />
        } @placeholder {
          <p class="muted">Dados fiscais…</p>
        }

        @defer (on viewport) {
          <app-part-listings [partId]="p.id" />
        } @placeholder {
          <p class="muted">Anúncios…</p>
        }

        @defer (on viewport) {
          <app-integration-log [partId]="p.id" />
        } @placeholder {
          <p class="muted">Integrações…</p>
        }
      }

      @if (notice()) {
        <p class="warn" role="status">{{ notice() }}</p>
      }

      @if (!part()) {
        <fieldset class="ai-assist">
          <legend>Preencher com IA</legend>
          <p class="muted">
            Envie de 1 a {{ maxSuggestionPhotos }} fotos da peça, de preferência com a etiqueta e o código à mostra. A IA sugere
            título, descrição, estado, códigos OEM e veículos compatíveis; você confere, informa o código interno e o preço e
            salva. As fotos viram as fotos da peça. Cada sugestão conta 1 no limite mensal de cadastros com IA do seu plano.
          </p>
          <div class="status-row">
            <label class="button-like">
              Escolher fotos
              <input type="file" accept="image/jpeg,image/png,image/webp" multiple (change)="choosePhotos($event)" [disabled]="busy()" hidden />
            </label>
            @if (pendingPhotos().length) {
              <small>{{ pendingPhotos().length }} foto(s): {{ photoNames() }}</small>
            }
          </div>
          <label>
            O que você já sabe sobre a peça (opcional)
            <textarea [formControl]="notes" rows="2" [maxlength]="maxSuggestionNotes"
              placeholder="Ex.: retirada de um Gol 2011 1.6; lado do motorista"></textarea>
          </label>
          <div>
            <button type="button" (click)="suggest()" [disabled]="pendingPhotos().length === 0 || busy()">
              {{ suggesting() ? 'Analisando as fotos…' : 'Preencher com IA' }}
            </button>
          </div>
          @if (suggestError()) {
            <p class="error" role="alert">{{ suggestError() }}</p>
          }
          @if (suggestedVehicles().length || unmatchedVehicles().length) {
            <div class="ai-suggested-vehicles">
              <span class="label">Veículos sugeridos pela IA — desmarque os que não servem</span>
              <ul class="choices">
                @for (s of suggestedVehicles(); track s.vehicle.vehicleVersionId; let i = $index) {
                  <li>
                    <label class="checkbox">
                      <input type="checkbox" [checked]="s.keep" (change)="toggleVehicle(i)" [disabled]="busy()" />
                      {{ s.vehicle.brand }} {{ s.vehicle.model }} · {{ s.vehicle.engine }} ·
                      {{ range(s.vehicle.yearFrom ?? s.vehicle.versionYearFrom, s.vehicle.yearFrom ? s.vehicle.yearTo : s.vehicle.versionYearTo) }}
                    </label>
                  </li>
                }
              </ul>
              @if (unmatchedVehicles().length) {
                <p class="muted">
                  A IA também citou, mas não estão na tabela de veículos (adicione à mão depois, se for o caso):
                  {{ unmatchedVehicles().join('; ') }}.
                </p>
              }
            </div>
          }
        </fieldset>
      }

      <form [formGroup]="form" (ngSubmit)="save()">
        @if (!part()) {
          <label>
            Código interno
            <input type="text" formControlName="internalCode" [maxlength]="limits.code" placeholder="Automático (P-000001, P-000002…)" />
            <small>Deixe em branco para gerar automaticamente. Único na loja; não muda depois (aparece em etiquetas e planilhas).</small>
          </label>
        }
        <label [class.ai-suggested]="aiFields().has('title')">
          Título
          <input type="text" formControlName="title" [maxlength]="limits.title" required />
          @if (aiFields().has('title')) {
            <span class="ai-note">Sugerido pela IA — confira. <button type="button" class="link" (click)="reviewed('title')">Revisado</button></span>
          }
        </label>
        <label [class.ai-suggested]="aiFields().has('description')">
          Descrição
          <textarea formControlName="description" rows="5" [maxlength]="limits.description"></textarea>
          @if (aiFields().has('description')) {
            <span class="ai-note">Sugerido pela IA — confira. <button type="button" class="link" (click)="reviewed('description')">Revisado</button></span>
          }
        </label>

        <div class="grid-3">
          <label [class.ai-suggested]="aiFields().has('condition')">
            Estado
            <select formControlName="condition">
              @for (c of conditions; track c) {
                <option [value]="c">{{ conditionLabels[c] }}</option>
              }
            </select>
            @if (aiFields().has('condition')) {
              <span class="ai-note">Sugerido pela IA. <button type="button" class="link" (click)="reviewed('condition')">Revisado</button></span>
            }
          </label>
          <label>
            Preço (R$)
            <input type="number" formControlName="price" min="0.01" step="0.01" required />
          </label>
          <label>
            Quantidade em estoque
            <input type="number" formControlName="quantity" min="0" step="1" required />
            @if (part()?.stock?.reserved) {
              <small>Mínimo {{ part()!.stock.reserved }}: há unidades reservadas para pedidos em pagamento.</small>
            }
          </label>
        </div>

        <fieldset>
          <legend>Embalagem (para frete)</legend>
          <div class="grid-4">
            <label>Comprimento (cm) <input type="number" formControlName="lengthCm" min="1" step="1" /></label>
            <label>Largura (cm) <input type="number" formControlName="widthCm" min="1" step="1" /></label>
            <label>Altura (cm) <input type="number" formControlName="heightCm" min="1" step="1" /></label>
            <label>Peso (g) <input type="number" formControlName="weightG" min="1" step="1" /></label>
          </div>
          @if (!hasDimensions()) {
            <p class="warn">Sem as quatro medidas, a peça não pode ser publicada em canais que calculam frete.</p>
          }
        </fieldset>

        <label [class.ai-suggested]="aiFields().has('oemCodes')">
          Códigos OEM
          <textarea formControlName="oemCodes" rows="3" placeholder="Um por linha (ex.: 5U0 941 015)"></textarea>
          <small>Espaços, pontos e hífens são ignorados na busca.</small>
          @if (aiFields().has('oemCodes')) {
            <span class="ai-note">Lidos pela IA nas fotos — confira com a peça. <button type="button" class="link" (click)="reviewed('oemCodes')">Revisado</button></span>
          }
        </label>

        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }
        <button type="submit" [disabled]="form.invalid || busy()">{{ part() ? 'Salvar' : 'Cadastrar' }}</button>
      </form>
    </main>
  `,
})
export class PartFormPage implements OnInit {
  /** Vem da rota (:id) com withComponentInputBinding; ausente em /pecas/nova. */
  readonly id = input<string>();

  private readonly api = inject(PartsApi);
  private readonly vehicles = inject(VehiclesApi);
  private readonly router = inject(Router);

  protected readonly limits = LIMITS;
  protected readonly conditions: PartCondition[] = ['used', 'new', 'refurbished'];
  protected readonly conditionLabels = CONDITION_LABELS;
  protected readonly statusLabels = STATUS_LABELS;
  protected readonly part = signal<PartView | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly form = inject(FormBuilder).group({
    internalCode: ['', [Validators.maxLength(LIMITS.code)]],
    title: ['', [Validators.required, Validators.maxLength(LIMITS.title)]],
    description: ['', [Validators.maxLength(LIMITS.description)]],
    condition: ['used' as PartCondition, Validators.required],
    price: [null as number | null, [Validators.required, Validators.min(0.01)]],
    quantity: [1 as number | null, [Validators.required, Validators.min(0), Validators.max(LIMITS.quantity)]],
    lengthCm: [null as number | null, [Validators.min(1), Validators.max(LIMITS.dimensionCm)]],
    widthCm: [null as number | null, [Validators.min(1), Validators.max(LIMITS.dimensionCm)]],
    heightCm: [null as number | null, [Validators.min(1), Validators.max(LIMITS.dimensionCm)]],
    weightG: [null as number | null, [Validators.min(1), Validators.max(LIMITS.weightG)]],
    oemCodes: [''],
  });

  private readonly values = toSignal(this.form.valueChanges, { initialValue: this.form.getRawValue() });

  protected readonly hasDimensions = computed(() => {
    const v = this.values();
    return [v.lengthCm, v.widthCm, v.heightCm, v.weightG].every((n) => n != null && n > 0);
  });

  // Cadastro por foto (RF30).
  protected readonly maxSuggestionPhotos = MAX_SUGGESTION_PHOTOS;
  protected readonly maxSuggestionNotes = MAX_SUGGESTION_NOTES;
  protected readonly range = yearRange;
  protected readonly notes = new FormControl('', { nonNullable: true, validators: [Validators.maxLength(MAX_SUGGESTION_NOTES)] });
  protected readonly pendingPhotos = signal<File[]>([]);
  protected readonly photoNames = computed(() => this.pendingPhotos().map((f) => f.name).join(', '));
  protected readonly suggesting = signal(false);
  protected readonly suggestError = signal<string | null>(null);
  protected readonly aiFields = signal<ReadonlySet<AiField>>(new Set());
  protected readonly suggestedVehicles = signal<{ vehicle: SuggestedCompatibility; keep: boolean }[]>([]);
  protected readonly unmatchedVehicles = signal<string[]>([]);
  /** Aviso vindo do cadastro (ex.: foto que não subiu), mostrado na página da peça. */
  protected readonly notice = signal<string | null>(null);

  constructor() {
    // Mexeu no campo = revisou.
    for (const field of AI_FIELDS) {
      (this.form.controls[field].valueChanges as Observable<unknown>).pipe(takeUntilDestroyed()).subscribe(() => this.reviewed(field));
    }
  }

  protected reviewed(field: AiField): void {
    if (!this.aiFields().has(field)) return;
    const next = new Set(this.aiFields());
    next.delete(field);
    this.aiFields.set(next);
  }

  protected choosePhotos(event: Event): void {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files ?? []);
    input.value = '';
    this.suggestError.set(null);
    const tooBig = files.find((f) => f.size > MAX_PHOTO_BYTES);
    if (files.length > MAX_SUGGESTION_PHOTOS) {
      this.suggestError.set(`Escolha no máximo ${MAX_SUGGESTION_PHOTOS} fotos.`);
    } else if (tooBig) {
      this.suggestError.set(`${tooBig.name}: a foto pode ter no máximo 10 MB.`);
    } else {
      this.pendingPhotos.set(files);
    }
  }

  async suggest(): Promise<void> {
    if (this.pendingPhotos().length === 0) return;
    this.busy.set(true);
    this.suggesting.set(true);
    this.suggestError.set(null);
    try {
      const suggestion = await this.api.suggest(this.pendingPhotos(), this.notes.value);
      this.form.patchValue({
        title: suggestion.title,
        description: suggestion.description,
        condition: suggestion.condition,
        oemCodes: suggestion.oemCodes.join('\n'),
      });
      // Depois do patch: o patch dispara valueChanges, que limparia o destaque.
      this.aiFields.set(new Set(AI_FIELDS.filter((f) => f !== 'oemCodes' || suggestion.oemCodes.length > 0)));
      this.suggestedVehicles.set(suggestion.compatibilities.map((vehicle) => ({ vehicle, keep: true })));
      this.unmatchedVehicles.set(suggestion.unmatchedVehicles);
    } catch (error) {
      this.suggestError.set(problemTitle(error));
    } finally {
      this.suggesting.set(false);
      this.busy.set(false);
    }
  }

  protected toggleVehicle(index: number): void {
    this.suggestedVehicles.update((list) => list.map((s, i) => (i === index ? { ...s, keep: !s.keep } : s)));
  }

  /** Depois de criar: fotos escolhidas para a IA e veículos mantidos. Falhas não desfazem a peça: viram aviso. */
  private async completeNewPart(partId: string): Promise<string | null> {
    const problems: string[] = [];
    for (const file of this.pendingPhotos()) {
      try {
        await this.api.addPhoto(partId, file);
      } catch (error) {
        problems.push(`Foto ${file.name}: ${problemTitle(error)}`);
      }
    }
    for (const { vehicle } of this.suggestedVehicles().filter((s) => s.keep)) {
      try {
        await this.vehicles.addCompatibility(partId, vehicle.vehicleVersionId, vehicle.yearFrom, vehicle.yearTo, true);
      } catch (error) {
        problems.push(`${vehicle.brand} ${vehicle.model} ${vehicle.engine}: ${problemTitle(error)}`);
      }
    }
    return problems.length ? `A peça foi cadastrada, mas: ${problems.join(' ')}` : null;
  }

  async ngOnInit(): Promise<void> {
    const id = this.id();
    const state = (typeof history !== 'undefined' ? history.state : null) as { notice?: string } | null;
    if (id && state?.notice) this.notice.set(state.notice);
    if (!id) return;
    try {
      this.fill(await this.api.get(id));
    } catch (error) {
      this.error.set(problemTitle(error, 'Peça não encontrada.'));
    }
  }

  async save(): Promise<void> {
    if (this.form.invalid) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const current = this.part();
      if (current) {
        this.fill(await this.api.update(current.id, this.request()));
      } else {
        const created = await this.api.create(this.request());
        const notice = await this.completeNewPart(created.id);
        await this.router.navigate(['/pecas', created.id], notice ? { state: { notice } } : undefined);
      }
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }

  protected readonly maxPhotos = MAX_PHOTOS;
  protected readonly uploading = signal<string | null>(null);
  protected readonly photoError = signal<string | null>(null);
  protected readonly sortedPhotos = computed(() => [...(this.part()?.photos ?? [])].sort((a, b) => a.position - b.position));

  /** Envia uma por vez (a API converte cada foto na hora); para no primeiro erro e mostra a mensagem. */
  async addPhotos(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files ?? []);
    input.value = '';
    const current = this.part();
    if (!current || files.length === 0) return;
    this.busy.set(true);
    this.photoError.set(null);
    try {
      for (const [index, file] of files.entries()) {
        if (file.size > MAX_PHOTO_BYTES) throw new Error(`${file.name}: a foto pode ter no máximo 10 MB.`);
        this.uploading.set(`${index + 1} de ${files.length}`);
        this.part.set(await this.api.addPhoto(current.id, file));
      }
    } catch (error) {
      this.photoError.set(error instanceof Error && !(error as { status?: number }).status ? error.message : problemTitle(error));
    } finally {
      this.uploading.set(null);
      this.busy.set(false);
    }
  }

  async removePhoto(photoId: string): Promise<void> {
    const current = this.part();
    if (!current || !confirm('Remover esta foto?')) return;
    await this.runPhoto(() => this.api.removePhoto(current.id, photoId));
  }

  async move(photoId: string, offset: -1 | 1): Promise<void> {
    const current = this.part();
    if (!current) return;
    const ids = this.sortedPhotos().map((p) => p.id);
    const from = ids.indexOf(photoId);
    [ids[from], ids[from + offset]] = [ids[from + offset], ids[from]];
    await this.runPhoto(() => this.api.reorderPhotos(current.id, ids));
  }

  private async runPhoto(action: () => Promise<PartView>): Promise<void> {
    this.busy.set(true);
    this.photoError.set(null);
    try {
      this.part.set(await action());
    } catch (error) {
      this.photoError.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }

  async setStatus(status: 'active' | 'inactive'): Promise<void> {
    const current = this.part();
    if (!current) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      this.part.set(await this.api.setStatus(current.id, status));
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }

  private request(): PartRequest {
    const v = this.form.getRawValue();
    const optional = (n: number | null) => (n == null || Number.isNaN(n) ? null : n);
    return {
      internalCode: this.part() ? undefined : (v.internalCode ?? '').trim(),
      title: (v.title ?? '').trim(),
      description: v.description ?? '',
      condition: v.condition ?? 'used',
      price: Number(v.price),
      quantity: Number(v.quantity),
      lengthCm: optional(v.lengthCm),
      widthCm: optional(v.widthCm),
      heightCm: optional(v.heightCm),
      weightG: optional(v.weightG),
      oemCodes: parseOemCodes(v.oemCodes ?? ''),
    };
  }

  private fill(part: PartView): void {
    this.part.set(part);
    this.form.reset({
      internalCode: part.internalCode,
      title: part.title,
      description: part.description,
      condition: part.condition,
      price: part.price,
      quantity: part.stock.onHand,
      lengthCm: part.lengthCm,
      widthCm: part.widthCm,
      heightCm: part.heightCm,
      weightG: part.weightG,
      oemCodes: part.oemCodes.join('\n'),
    });
  }
}
