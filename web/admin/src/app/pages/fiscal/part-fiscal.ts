import { Component, effect, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { problemTitle } from '../../core/auth';
import { PartView } from '../parts/parts.api';
import { FiscalApi } from './fiscal.api';
import { ORIGINS } from './fiscal-settings';

/** Dados fiscais da peça (RF19 CA3). Vazio = padrão da loja (Dados da loja → Nota fiscal). */
@Component({
  selector: 'app-part-fiscal',
  imports: [FormsModule],
  template: `
    <section data-part-fiscal>
      <h2>Dados fiscais</h2>
      <p class="muted">Deixe em branco para usar o padrão da loja.</p>
      <div class="grid-3">
        <label>NCM <input [(ngModel)]="ncm" name="ncm" maxlength="10" placeholder="padrão da loja" data-part-ncm /></label>
        <label>CSOSN / CST <input [(ngModel)]="taxCode" name="taxCode" maxlength="3" placeholder="padrão da loja" data-part-tax /></label>
        <label>
          Origem
          <select [(ngModel)]="origin" name="origin" data-part-origin>
            <option [ngValue]="null">Padrão da loja</option>
            @for (o of origins; track o.value) {
              <option [ngValue]="o.value">{{ o.label }}</option>
            }
          </select>
        </label>
      </div>
      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
      <div class="order-actions">
        <button type="button" (click)="save()" [disabled]="busy()" data-save-part-fiscal>Salvar dados fiscais</button>
      </div>
    </section>
  `,
})
export class PartFiscal {
  readonly part = input.required<PartView>();
  readonly changed = output<PartView>();

  private readonly api = inject(FiscalApi);

  protected readonly origins = ORIGINS;
  protected ncm = '';
  protected taxCode = '';
  protected origin: number | null = null;
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  constructor() {
    effect(() => {
      const f = this.part().fiscal;
      this.ncm = f?.ncm ?? '';
      this.taxCode = f?.taxCode ?? '';
      this.origin = f?.origin ?? null;
    });
  }

  protected async save(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      const updated = await this.api.setPartFiscal(this.part().id, {
        ncm: this.ncm.trim() || null,
        taxCode: this.taxCode.trim() || null,
        origin: this.origin,
      });
      this.changed.emit(updated);
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }
}
