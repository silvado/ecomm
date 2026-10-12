import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { problemTitle } from '../../core/auth';
import { DonorsApi, DonorVehiclePage } from './donors.api';

/** Veículos doadores da loja (RF10): de onde saem as peças usadas. Dados internos: não aparecem na loja. */
@Component({
  selector: 'app-donors-list',
  imports: [FormsModule, RouterLink],
  template: `
    <main class="card wide">
      <div class="header-row">
        <h1>Veículos doadores</h1>
        <a routerLink="/doadores/novo" class="button-like">Novo veículo</a>
      </div>
      <p class="muted">
        Cadastre cada veículo que entra para desmontagem (com a NF de entrada e a certidão de baixa) e ligue a ele as peças retiradas.
        Estes dados ficam só no painel: nunca aparecem na loja nem nos canais de venda.
      </p>
      <div class="filters">
        <input type="search" placeholder="Placa, chassi, Renavam, marca ou modelo" [(ngModel)]="search" (ngModelChange)="searchChanged()" aria-label="Buscar veículos" />
      </div>
      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
      <table>
        <thead>
          <tr>
            <th>Veículo</th>
            <th>Chassi</th>
            <th>Renavam</th>
            <th class="num">Peças</th>
            <th>Documentação</th>
          </tr>
        </thead>
        <tbody>
          @for (v of page()?.items ?? []; track v.id) {
            <tr>
              <td><a [routerLink]="['/doadores', v.id]">{{ v.label }}</a></td>
              <td>{{ v.chassis ?? '—' }}</td>
              <td>{{ v.renavam ?? '—' }}</td>
              <td class="num">{{ v.partCount }}</td>
              <td>
                @if (v.missingForDismantler.length) {
                  <span class="badge warn" [title]="'Falta: ' + v.missingForDismantler.join(', ')">Incompleta</span>
                } @else {
                  <span class="badge ok">Completa</span>
                }
              </td>
            </tr>
          } @empty {
            <tr>
              <td colspan="5" class="muted">{{ loading() ? 'Carregando…' : search.trim() ? 'Nenhum veículo encontrado.' : 'Nenhum veículo cadastrado ainda.' }}</td>
            </tr>
          }
        </tbody>
      </table>
      @if (page(); as p) {
        @if (p.total > p.pageSize) {
          <div class="pager">
            <button type="button" class="link" [disabled]="p.page <= 1" (click)="load(p.page - 1)">Anterior</button>
            <span class="muted">Página {{ p.page }} · {{ p.total }} veículos</span>
            <button type="button" class="link" [disabled]="p.page * p.pageSize >= p.total" (click)="load(p.page + 1)">Próxima</button>
          </div>
        }
      }
    </main>
  `,
})
export class DonorsListPage implements OnInit {
  private readonly api = inject(DonorsApi);
  private debounce?: ReturnType<typeof setTimeout>;

  protected search = '';
  protected readonly page = signal<DonorVehiclePage | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    await this.load(1);
  }

  protected searchChanged(): void {
    clearTimeout(this.debounce);
    this.debounce = setTimeout(() => void this.load(1), 300);
  }

  protected async load(pageNumber: number): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.page.set(await this.api.search(this.search, pageNumber));
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.loading.set(false);
    }
  }
}
