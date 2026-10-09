import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { problemTitle } from '../../core/auth';
import { CONDITION_LABELS, parseOemCodes, PartCondition, PartRequest, PartsApi, PartView, STATUS_LABELS } from './parts.api';

/** Mesmos limites do domínio (Part). */
const LIMITS = { code: 60, title: 120, description: 5000, dimensionCm: 1000, weightG: 1_000_000, quantity: 1_000_000 };

@Component({
  selector: 'app-part-form',
  imports: [ReactiveFormsModule, RouterLink],
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

      <form [formGroup]="form" (ngSubmit)="save()">
        @if (!part()) {
          <label>
            Código interno
            <input type="text" formControlName="internalCode" [maxlength]="limits.code" required />
            <small>Único na loja. Não muda depois (aparece em etiquetas e planilhas).</small>
          </label>
        }
        <label>
          Título
          <input type="text" formControlName="title" [maxlength]="limits.title" required />
        </label>
        <label>
          Descrição
          <textarea formControlName="description" rows="5" [maxlength]="limits.description"></textarea>
        </label>

        <div class="grid-3">
          <label>
            Estado
            <select formControlName="condition">
              @for (c of conditions; track c) {
                <option [value]="c">{{ conditionLabels[c] }}</option>
              }
            </select>
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

        <label>
          Códigos OEM
          <textarea formControlName="oemCodes" rows="3" placeholder="Um por linha (ex.: 5U0 941 015)"></textarea>
          <small>Espaços, pontos e hífens são ignorados na busca.</small>
        </label>

        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }
        @if (saved()) {
          <p class="ok" role="status">Salvo.</p>
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
  private readonly router = inject(Router);

  protected readonly limits = LIMITS;
  protected readonly conditions: PartCondition[] = ['used', 'new', 'refurbished'];
  protected readonly conditionLabels = CONDITION_LABELS;
  protected readonly statusLabels = STATUS_LABELS;
  protected readonly part = signal<PartView | null>(null);
  protected readonly busy = signal(false);
  protected readonly saved = signal(false);
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

  async ngOnInit(): Promise<void> {
    const id = this.id();
    if (!id) {
      this.form.controls.internalCode.addValidators(Validators.required);
      this.form.controls.internalCode.updateValueAndValidity();
      return;
    }
    try {
      this.fill(await this.api.get(id));
    } catch (error) {
      this.error.set(problemTitle(error, 'Peça não encontrada.'));
    }
  }

  async save(): Promise<void> {
    if (this.form.invalid) return;
    this.busy.set(true);
    this.saved.set(false);
    this.error.set(null);
    try {
      const current = this.part();
      if (current) {
        this.fill(await this.api.update(current.id, this.request()));
        this.saved.set(true);
      } else {
        const created = await this.api.create(this.request());
        await this.router.navigate(['/pecas', created.id]);
      }
    } catch (error) {
      this.error.set(problemTitle(error));
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
