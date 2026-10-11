import { Component, inject, input, OnInit, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { problemTitle } from '../../core/auth';
import { STATUS_LABELS } from '../parts/parts.api';
import { DonorsApi, DonorVehicleRequest, DonorVehicleView } from './donors.api';

/**
 * Cadastro do veículo doador (RF10). Placa (antiga ou Mercosul) ou chassi identificam o veículo; Renavam, certidão de
 * baixa e chave da NF de entrada completam o que o modo desmontadora (Lei 12.977) exige para pôr as peças à venda.
 */
@Component({
  selector: 'app-donor-form',
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <main class="card">
      <p><a routerLink="/doadores">← Veículos doadores</a></p>
      <h1>{{ vehicle()?.label ?? 'Novo veículo doador' }}</h1>

      @if (vehicle(); as v) {
        @if (v.missingForDismantler.length) {
          <p class="warn" role="status">Documentação incompleta para o modo desmontadora: falta {{ v.missingForDismantler.join(', ') }}.</p>
        }
      }

      <form [formGroup]="form" (ngSubmit)="save()">
        <div class="grid-3">
          <label>Marca <input formControlName="brand" maxlength="60" required /></label>
          <label>Modelo <input formControlName="model" maxlength="60" required /></label>
          <label>Ano-modelo <input type="number" formControlName="modelYear" min="1900" step="1" /></label>
        </div>
        <div class="grid-3">
          <label>Placa <input formControlName="plate" maxlength="8" placeholder="ABC1D23" /></label>
          <label>Chassi <input formControlName="chassis" maxlength="17" /></label>
          <label>Renavam <input formControlName="renavam" inputmode="numeric" maxlength="11" /></label>
        </div>
        <small>Informe a placa ou o chassi (ou os dois).</small>
        <div class="grid-3">
          <label>Cor <input formControlName="color" maxlength="30" /></label>
          <label>Certidão de baixa (nº) <input formControlName="deregistrationCertificate" maxlength="40" /></label>
          <label>Data de entrada <input type="date" formControlName="entryDate" /></label>
        </div>
        <label>
          Chave da NF de entrada
          <input formControlName="entryInvoiceKey" inputmode="numeric" maxlength="54" placeholder="44 dígitos" />
        </label>
        <label>Observações <textarea formControlName="notes" rows="3" maxlength="1000"></textarea></label>

        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }
        @if (saved()) {
          <p class="ok" role="status">Salvo.</p>
        }
        <div class="order-actions">
          <button type="submit" [disabled]="form.invalid || busy()">{{ vehicle() ? 'Salvar' : 'Cadastrar' }}</button>
          @if (vehicle(); as v) {
            @if (!v.partCount) {
              <button type="button" class="link danger" (click)="remove(v)" [disabled]="busy()">Excluir veículo</button>
            }
          }
        </div>
      </form>

      @if (vehicle(); as v) {
        <h2>Peças deste veículo ({{ v.partCount }})</h2>
        @if (v.parts.length) {
          <table>
            <tbody>
              @for (p of v.parts; track p.id) {
                <tr>
                  <td><a [routerLink]="['/pecas', p.id]">{{ p.internalCode }}</a></td>
                  <td>{{ p.title }}</td>
                  <td>{{ p.traceabilityTag ?? 'sem etiqueta' }}</td>
                  <td>{{ statusLabels[p.status] }}</td>
                </tr>
              }
            </tbody>
          </table>
        } @else {
          <p class="muted">Nenhuma peça ligada ainda. Ligue as peças na seção "Rastreabilidade" do cadastro de cada peça.</p>
        }
      }
    </main>
  `,
})
export class DonorFormPage implements OnInit {
  /** Vem da rota (:id); ausente em /doadores/novo. */
  readonly id = input<string>();

  private readonly api = inject(DonorsApi);
  private readonly router = inject(Router);

  protected readonly statusLabels = STATUS_LABELS;
  protected readonly vehicle = signal<DonorVehicleView | null>(null);
  protected readonly busy = signal(false);
  protected readonly saved = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    brand: ['', [Validators.required, Validators.maxLength(60)]],
    model: ['', [Validators.required, Validators.maxLength(60)]],
    modelYear: [null as number | null],
    plate: [''],
    chassis: [''],
    renavam: [''],
    color: [''],
    deregistrationCertificate: [''],
    entryDate: [''],
    entryInvoiceKey: [''],
    notes: [''],
  });

  async ngOnInit(): Promise<void> {
    const id = this.id();
    if (!id) return;
    try {
      this.fill(await this.api.get(id));
    } catch (error) {
      this.error.set(problemTitle(error, 'Veículo não encontrado.'));
    }
  }

  protected async save(): Promise<void> {
    if (this.form.invalid) return;
    this.busy.set(true);
    this.saved.set(false);
    this.error.set(null);
    try {
      const current = this.vehicle();
      if (current) {
        this.fill(await this.api.update(current.id, this.request()));
        this.saved.set(true);
      } else {
        const created = await this.api.create(this.request());
        await this.router.navigate(['/doadores', created.id]);
      }
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }

  protected async remove(v: DonorVehicleView): Promise<void> {
    if (!confirm(`Excluir ${v.label}?`)) return;
    this.busy.set(true);
    try {
      await this.api.remove(v.id);
      await this.router.navigate(['/doadores']);
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }

  private request(): DonorVehicleRequest {
    const v = this.form.getRawValue();
    const text = (s: string) => (s.trim() ? s.trim() : null);
    return {
      brand: v.brand.trim(),
      model: v.model.trim(),
      modelYear: v.modelYear == null || Number.isNaN(Number(v.modelYear)) ? null : Number(v.modelYear),
      plate: text(v.plate),
      chassis: text(v.chassis),
      renavam: text(v.renavam),
      color: text(v.color),
      deregistrationCertificate: text(v.deregistrationCertificate),
      entryDate: text(v.entryDate),
      entryInvoiceKey: text(v.entryInvoiceKey),
      notes: text(v.notes),
    };
  }

  private fill(v: DonorVehicleView): void {
    this.vehicle.set(v);
    this.form.reset({
      brand: v.brand,
      model: v.model,
      modelYear: v.modelYear,
      plate: v.plate ?? '',
      chassis: v.chassis ?? '',
      renavam: v.renavam ?? '',
      color: v.color ?? '',
      deregistrationCertificate: v.deregistrationCertificate ?? '',
      entryDate: v.entryDate ?? '',
      entryInvoiceKey: v.entryInvoiceKey ?? '',
      notes: v.notes ?? '',
    });
  }
}
