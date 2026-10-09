import { Component, inject, input, OnInit, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { problemTitle } from '../../core/auth';
import { PartView } from './parts.api';
import { CompatibilityView, VehicleOption, VehiclesApi, VehicleVersionOption, yearRange } from './vehicles.api';

/** Veículos em que a peça serve (RF09): seleção marca → modelo → versão, anos opcionais dentro da versão. */
@Component({
  selector: 'app-compatibilities',
  imports: [FormsModule],
  template: `
    <section class="compatibilities">
      <h2>Compatibilidade</h2>
      @if (compatibilities().length === 0) {
        <p class="muted">Nenhum veículo informado: a peça não aparece na busca por veículo da loja.</p>
      } @else {
        <ul>
          @for (c of compatibilities(); track c.id) {
            <li>
              <span>
                {{ c.brand }} {{ c.model }} · {{ c.engine }} · {{ range(c.yearFrom, c.yearTo) }}
                @if (c.narrowed) {
                  <span class="badge">anos restritos</span>
                }
                @if (c.discontinued) {
                  <span class="badge warn" title="Saiu da tabela de veículos; a compatibilidade continua valendo">descontinuada</span>
                }
              </span>
              <button type="button" class="link danger" (click)="remove(c)" [disabled]="busy()">Remover</button>
            </li>
          }
        </ul>
      }

      <div class="compat-form">
        <label>
          Marca
          <select [(ngModel)]="brandId" (ngModelChange)="brandChanged()" [disabled]="busy()">
            <option value="">Escolha</option>
            @for (b of brands(); track b.id) {
              <option [value]="b.id">{{ b.name }}</option>
            }
          </select>
        </label>
        <label>
          Modelo
          <select [(ngModel)]="modelId" (ngModelChange)="modelChanged()" [disabled]="!brandId || busy()">
            <option value="">Escolha</option>
            @for (m of models(); track m.id) {
              <option [value]="m.id">{{ m.name }}</option>
            }
          </select>
        </label>
        <label>
          Versão
          <select [(ngModel)]="versionId" (ngModelChange)="versionChanged()" [disabled]="!modelId || busy()">
            <option value="">Escolha</option>
            @for (v of versions(); track v.id) {
              <option [value]="v.id">{{ v.engine }} · {{ range(v.yearFrom, v.yearTo) }}</option>
            }
          </select>
        </label>
        <label>
          De
          <input type="number" [(ngModel)]="yearFrom" [disabled]="!versionId || busy()" placeholder="todos" />
        </label>
        <label>
          Até
          <input type="number" [(ngModel)]="yearTo" [disabled]="!versionId || busy()" placeholder="todos" />
        </label>
        <button type="button" (click)="add()" [disabled]="!versionId || busy()">Adicionar</button>
      </div>
      <small>Deixe os anos em branco para a versão inteira; informe os dois para restringir dentro dela.</small>
      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
    </section>
  `,
})
export class Compatibilities implements OnInit {
  readonly partId = input.required<string>();
  readonly compatibilities = input.required<CompatibilityView[]>();
  /** Peça atualizada depois de cada alteração. */
  readonly changed = output<PartView>();

  private readonly api = inject(VehiclesApi);

  protected readonly range = yearRange;
  protected readonly brands = signal<VehicleOption[]>([]);
  protected readonly models = signal<VehicleOption[]>([]);
  protected readonly versions = signal<VehicleVersionOption[]>([]);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected brandId = '';
  protected modelId = '';
  protected versionId = '';
  protected yearFrom: number | null = null;
  protected yearTo: number | null = null;

  async ngOnInit(): Promise<void> {
    await this.run(async () => this.brands.set(await this.api.brands()));
  }

  protected async brandChanged(): Promise<void> {
    this.modelId = '';
    this.resetVersion();
    this.models.set([]);
    if (this.brandId) await this.run(async () => this.models.set(await this.api.models(this.brandId)));
  }

  protected async modelChanged(): Promise<void> {
    this.resetVersion();
    if (this.modelId) await this.run(async () => this.versions.set(await this.api.versions(this.modelId)));
  }

  protected versionChanged(): void {
    this.yearFrom = null;
    this.yearTo = null;
  }

  async add(): Promise<void> {
    await this.run(async () => {
      this.changed.emit(await this.api.addCompatibility(this.partId(), this.versionId, this.yearFrom || null, this.yearTo || null));
      this.yearFrom = null;
      this.yearTo = null;
    });
  }

  async remove(c: CompatibilityView): Promise<void> {
    await this.run(async () => this.changed.emit(await this.api.removeCompatibility(this.partId(), c.id)));
  }

  private resetVersion(): void {
    this.versionId = '';
    this.versions.set([]);
    this.versionChanged();
  }

  private async run(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await action();
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }
}
