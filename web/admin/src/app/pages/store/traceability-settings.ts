import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { problemTitle } from '../../core/auth';
import { DonorsApi, TraceabilityField } from '../donors/donors.api';

/**
 * Rastreabilidade (RF10), em Dados da loja: modo desmontadora (Lei 12.977) e campos extras das peças. Campos que
 * continuam mantêm a chave, então os valores já preenchidos nas peças não se perdem.
 */
@Component({
  selector: 'app-traceability-settings',
  imports: [FormsModule],
  template: `
    <section data-traceability-settings>
      <h2>Rastreabilidade de peças usadas</h2>
      <label class="checkbox">
        <input type="checkbox" [(ngModel)]="dismantlerMode" name="dismantlerMode" data-dismantler />
        Empresa de desmontagem (Lei 12.977): exigir veículo doador completo, etiqueta e os campos obrigatórios para pôr peça usada à venda
      </label>
      <p class="muted">Sem marcar, os dados de rastreabilidade são opcionais. Peças já à venda não são retiradas ao ligar.</p>

      <h3>Campos extras</h3>
      @for (f of fields; track $index; let i = $index) {
        <div class="order-actions">
          <input [(ngModel)]="f.label" [name]="'label' + i" maxlength="40" placeholder="Ex.: Nº do registro no Detran" [attr.aria-label]="'Nome do campo ' + (i + 1)" />
          <label class="checkbox"><input type="checkbox" [(ngModel)]="f.required" [name]="'required' + i" /> Obrigatório</label>
          <button type="button" class="link danger" (click)="fields.splice(i, 1)">Remover</button>
        </div>
      } @empty {
        <p class="muted">Nenhum campo extra.</p>
      }
      @if (fields.length < 10) {
        <button type="button" class="link" (click)="fields.push({ key: '', label: '', required: false })" data-add-field>+ Adicionar campo</button>
      }

      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
      <div class="order-actions">
        <button type="button" (click)="save()" [disabled]="busy()" data-save-settings>Salvar rastreabilidade</button>
      </div>
    </section>
  `,
})
export class TraceabilitySettingsSection implements OnInit {
  private readonly api = inject(DonorsApi);

  protected dismantlerMode = false;
  protected fields: TraceabilityField[] = [];
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    try {
      this.apply(await this.api.settings());
    } catch (error) {
      this.error.set(problemTitle(error));
    }
  }

  protected async save(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      this.apply(await this.api.saveSettings({ dismantlerMode: this.dismantlerMode, fields: this.fields.map((f) => ({ ...f, label: f.label.trim() })) }));
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }

  private apply(settings: { dismantlerMode: boolean; fields: TraceabilityField[] }): void {
    this.dismantlerMode = settings.dismantlerMode;
    this.fields = settings.fields.map((f) => ({ ...f }));
  }
}
