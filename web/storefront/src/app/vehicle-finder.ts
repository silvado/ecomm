import { Component, inject, input, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { CatalogApi, SearchParams, VehicleOption } from './catalog';

/**
 * Busca da vitrine (RF13 CA3): texto (título, código ou OEM) e veículo (marca → modelo → ano). Funciona sem
 * JavaScript também: é um formulário GET para /busca; com JavaScript, navega sem recarregar a página.
 */
@Component({
  selector: 'app-vehicle-finder',
  imports: [FormsModule],
  template: `
    <form class="finder" action="/busca" method="get" (submit)="submit($event)">
      <label class="field wide">
        <span>O que você procura?</span>
        <input type="search" name="q" [(ngModel)]="q" placeholder="Peça, código ou OEM" />
      </label>
      <label class="field">
        <span>Marca</span>
        <select name="marca" [(ngModel)]="brand" (ngModelChange)="brandChanged()">
          <option value="">Qualquer</option>
          @for (b of brands(); track b.id) {
            <option [value]="b.id">{{ b.name }}</option>
          }
        </select>
      </label>
      <label class="field">
        <span>Modelo</span>
        <select name="modelo" [(ngModel)]="model" [disabled]="!brand">
          <option value="">Qualquer</option>
          @for (m of models(); track m.id) {
            <option [value]="m.id">{{ m.name }}</option>
          }
        </select>
      </label>
      <label class="field narrow">
        <span>Ano</span>
        <input type="number" name="ano" [(ngModel)]="year" [disabled]="!model" inputmode="numeric" min="1950" max="2100" />
      </label>
      <button type="submit">Buscar</button>
    </form>
  `,
})
export class VehicleFinder implements OnInit {
  readonly initial = input<SearchParams>({});

  private readonly api = inject(CatalogApi);
  private readonly router = inject(Router);

  protected readonly brands = signal<VehicleOption[]>([]);
  protected readonly models = signal<VehicleOption[]>([]);
  protected q = '';
  protected brand = '';
  protected model = '';
  protected year: number | null = null;

  ngOnInit(): void {
    const initial = this.initial();
    this.q = initial.q ?? '';
    this.brand = initial.marca ?? '';
    this.model = initial.modelo ?? '';
    this.year = initial.ano ?? null;
    this.api.brands().subscribe({ next: (brands) => this.brands.set(brands), error: () => this.brands.set([]) });
    if (this.brand) this.loadModels();
  }

  protected brandChanged(): void {
    this.model = '';
    this.year = null;
    this.models.set([]);
    if (this.brand) this.loadModels();
  }

  protected submit(event: Event): void {
    event.preventDefault();
    const queryParams: Record<string, string | number> = {};
    if (this.q.trim()) queryParams['q'] = this.q.trim();
    if (this.brand) queryParams['marca'] = this.brand;
    if (this.model) queryParams['modelo'] = this.model;
    if (this.model && this.year) queryParams['ano'] = this.year;
    void this.router.navigate(['/busca'], { queryParams });
  }

  private loadModels(): void {
    this.api.models(this.brand).subscribe({ next: (models) => this.models.set(models), error: () => this.models.set([]) });
  }
}
