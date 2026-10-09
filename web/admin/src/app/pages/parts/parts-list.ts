import { CurrencyPipe } from '@angular/common';
import { Component, inject, OnDestroy, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { problemTitle } from '../../core/auth';
import { CONDITION_LABELS, PartPage, PartsApi, PartStatus, STATUS_LABELS } from './parts.api';
import { PhotoThumb } from './photo-thumb';
import { VehicleOption, VehiclesApi } from './vehicles.api';

@Component({
  selector: 'app-parts-list',
  imports: [FormsModule, RouterLink, CurrencyPipe, PhotoThumb],
  template: `
    <main class="card wide">
      <div class="header-row">
        <h1>Peças</h1>
        <a routerLink="/pecas/nova" class="button-like">Nova peça</a>
      </div>

      <div class="filters">
        <input
          type="search"
          placeholder="Buscar por título, código ou OEM"
          [(ngModel)]="search"
          (ngModelChange)="searchChanged()"
          aria-label="Buscar peças"
        />
        <select [(ngModel)]="status" (ngModelChange)="load(1)" aria-label="Situação">
          <option value="">Todas as situações</option>
          @for (s of statuses; track s) {
            <option [value]="s">{{ statusLabels[s] }}</option>
          }
        </select>
      </div>
      <div class="filters">
        <select [(ngModel)]="brandId" (ngModelChange)="brandChanged()" aria-label="Marca do veículo">
          <option value="">Qualquer veículo</option>
          @for (b of brands(); track b.id) {
            <option [value]="b.id">{{ b.name }}</option>
          }
        </select>
        <select [(ngModel)]="modelId" (ngModelChange)="load(1)" [disabled]="!brandId" aria-label="Modelo do veículo">
          <option value="">Todos os modelos</option>
          @for (m of models(); track m.id) {
            <option [value]="m.id">{{ m.name }}</option>
          }
        </select>
        <input type="number" [(ngModel)]="year" (ngModelChange)="searchChanged()" [disabled]="!modelId" placeholder="Ano-modelo" aria-label="Ano-modelo" />
      </div>

      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }

      <table>
        <thead>
          <tr>
            <th><span class="sr-only">Foto</span></th>
            <th>Código</th>
            <th>Título</th>
            <th>Estado</th>
            <th class="num">Preço</th>
            <th class="num">Disponível / físico</th>
            <th>Situação</th>
          </tr>
        </thead>
        <tbody>
          @for (part of page()?.items ?? []; track part.id) {
            <tr>
              <td class="thumb-cell">
                @if (part.coverPhotoId) {
                  <app-photo-thumb [partId]="part.id" [photoId]="part.coverPhotoId" [size]="48" alt="" />
                }
              </td>
              <td><a [routerLink]="['/pecas', part.id]">{{ part.internalCode }}</a></td>
              <td>
                {{ part.title }}
                @if (!part.hasShippingDimensions) {
                  <span class="badge warn" title="Sem medidas e peso da embalagem: não publica em canal com frete calculado">sem medidas</span>
                }
              </td>
              <td>{{ conditionLabels[part.condition] }}</td>
              <td class="num">{{ part.price | currency: 'BRL' : 'symbol' : '1.2-2' : 'pt-BR' }}</td>
              <td class="num">{{ part.stock.available }} / {{ part.stock.onHand }}</td>
              <td><span class="badge" [class.ok]="part.status === 'active'">{{ statusLabels[part.status] }}</span></td>
            </tr>
          } @empty {
            <tr>
              <td colspan="7" class="muted">{{ loading() ? 'Carregando…' : 'Nenhuma peça encontrada.' }}</td>
            </tr>
          }
        </tbody>
      </table>

      @if (page(); as p) {
        @if (p.total > p.pageSize) {
          <div class="pager">
            <button type="button" class="link" [disabled]="p.page <= 1" (click)="load(p.page - 1)">Anterior</button>
            <span class="muted">Página {{ p.page }} de {{ pages(p) }} · {{ p.total }} peças</span>
            <button type="button" class="link" [disabled]="p.page >= pages(p)" (click)="load(p.page + 1)">Próxima</button>
          </div>
        }
      }
    </main>
  `,
})
export class PartsListPage implements OnInit, OnDestroy {
  private readonly api = inject(PartsApi);
  private readonly vehicles = inject(VehiclesApi);
  private debounce?: ReturnType<typeof setTimeout>;

  protected readonly statuses: PartStatus[] = ['draft', 'active', 'inactive'];
  protected readonly statusLabels = STATUS_LABELS;
  protected readonly conditionLabels = CONDITION_LABELS;
  protected search = '';
  protected status: PartStatus | '' = '';
  protected brandId = '';
  protected modelId = '';
  protected year: number | null = null;
  protected readonly brands = signal<VehicleOption[]>([]);
  protected readonly models = signal<VehicleOption[]>([]);
  protected readonly page = signal<PartPage | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    await this.load(1);
    try {
      this.brands.set(await this.vehicles.brands());
    } catch {
      // Sem tabela de veículos o filtro só fica vazio.
    }
  }

  protected async brandChanged(): Promise<void> {
    this.modelId = '';
    this.year = null;
    this.models.set(this.brandId ? await this.vehicles.models(this.brandId) : []);
    await this.load(1);
  }

  ngOnDestroy(): void {
    clearTimeout(this.debounce);
  }

  protected searchChanged(): void {
    clearTimeout(this.debounce);
    this.debounce = setTimeout(() => void this.load(1), 300);
  }

  protected pages(p: PartPage): number {
    return Math.max(1, Math.ceil(p.total / p.pageSize));
  }

  async load(pageNumber: number): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.page.set(await this.api.search(this.search, this.status, pageNumber, 25, { modelId: this.modelId, year: this.year }));
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.loading.set(false);
    }
  }
}
