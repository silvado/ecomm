import { Component, effect, inject, input, OnInit, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { problemTitle } from '../../core/auth';
import { DonorsApi, DonorVehicleView, TraceabilitySettings } from '../donors/donors.api';
import { PartView } from './parts.api';

/**
 * Origem da peça (RF10): veículo doador, etiqueta de rastreabilidade e campos extras da loja. Só no painel. No modo
 * desmontadora, mostra o que ainda falta para a peça usada poder ir à venda.
 */
@Component({
  selector: 'app-part-traceability',
  imports: [FormsModule, RouterLink],
  template: `
    <section class="traceability" data-traceability>
      <h2>Rastreabilidade</h2>
      <p class="muted">Origem da peça. Fica só no painel: não aparece na loja nem nos canais de venda.</p>
      @if (part().traceability.missingForSale.length) {
        <p class="warn" role="status" data-missing>
          Modo desmontadora: para pôr à venda, falta {{ part().traceability.missingForSale.join('; ') }}.
        </p>
      }

      <div class="grid-3">
        <div>
          <span class="label">Veículo doador</span>
          @if (donorId) {
            <p>
              <a [routerLink]="['/doadores', donorId]">{{ donorLabel }}</a>
              · <button type="button" class="link danger" (click)="unlink()" [disabled]="busy()">Desligar</button>
            </p>
          } @else {
            <input type="search" placeholder="Placa, chassi ou modelo" [(ngModel)]="search" (ngModelChange)="searchChanged()" aria-label="Buscar veículo doador" />
            @if (results().length) {
              <ul class="choices">
                @for (v of results(); track v.id) {
                  <li><button type="button" class="link" (click)="choose(v)" [attr.data-donor]="v.id">{{ v.label }}</button></li>
                }
              </ul>
            } @else if (search.trim() && !searching()) {
              <small>Nenhum veículo encontrado. <a routerLink="/doadores/novo">Cadastrar veículo</a></small>
            }
          }
        </div>
        <label>
          Etiqueta de rastreabilidade
          <input [(ngModel)]="tag" name="tag" maxlength="40" />
        </label>
        @for (f of settings()?.fields ?? []; track f.key) {
          <label>
            {{ f.label }}{{ f.required ? ' *' : '' }}
            <input [(ngModel)]="fields[f.key]" [name]="'campo-' + f.key" maxlength="200" [attr.data-field]="f.key" />
          </label>
        }
      </div>
      @if (settings()?.fields?.length) {
        <small>* obrigatório no modo desmontadora.</small>
      }
      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
      @if (saved()) {
        <p class="ok" role="status">Rastreabilidade salva.</p>
      }
      <div class="order-actions">
        <button type="button" (click)="save()" [disabled]="busy()" data-save-traceability>Salvar rastreabilidade</button>
      </div>
    </section>
  `,
})
export class PartTraceability implements OnInit {
  readonly part = input.required<PartView>();
  readonly changed = output<PartView>();

  private readonly api = inject(DonorsApi);
  private debounce?: ReturnType<typeof setTimeout>;

  protected donorId: string | null = null;
  protected donorLabel: string | null = null;
  protected tag = '';
  protected fields: Record<string, string> = {};
  protected search = '';
  protected readonly settings = signal<TraceabilitySettings | null>(null);
  protected readonly results = signal<DonorVehicleView[]>([]);
  protected readonly searching = signal(false);
  protected readonly busy = signal(false);
  protected readonly saved = signal(false);
  protected readonly error = signal<string | null>(null);

  constructor() {
    // Atualiza o formulário quando a peça muda (ex.: depois de salvar).
    effect(() => {
      const t = this.part().traceability;
      this.donorId = t.donorVehicleId;
      this.donorLabel = t.donorVehicleLabel;
      this.tag = t.tag ?? '';
      this.fields = { ...t.customFields };
    });
  }

  async ngOnInit(): Promise<void> {
    try {
      this.settings.set(await this.api.settings());
    } catch (error) {
      this.error.set(problemTitle(error));
    }
  }

  protected searchChanged(): void {
    clearTimeout(this.debounce);
    this.debounce = setTimeout(() => void this.find(), 300);
  }

  protected choose(v: DonorVehicleView): void {
    this.donorId = v.id;
    this.donorLabel = v.label;
    this.results.set([]);
    this.search = '';
  }

  protected unlink(): void {
    this.donorId = null;
    this.donorLabel = null;
  }

  protected async save(): Promise<void> {
    this.busy.set(true);
    this.saved.set(false);
    this.error.set(null);
    try {
      const customFields = Object.fromEntries(Object.entries(this.fields).filter(([, v]) => v?.trim()).map(([k, v]) => [k, v.trim()]));
      const updated = await this.api.setPartTraceability(this.part().id, { donorVehicleId: this.donorId, tag: this.tag.trim() || null, customFields });
      this.saved.set(true);
      this.changed.emit(updated);
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }

  private async find(): Promise<void> {
    if (!this.search.trim()) {
      this.results.set([]);
      return;
    }
    this.searching.set(true);
    try {
      this.results.set((await this.api.search(this.search, 1, 8)).items);
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.searching.set(false);
    }
  }
}
