import { HttpClient } from '@angular/common/http';
import { Component, inject, Injectable, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { problemTitle } from '../../core/auth';

export interface DailySummarySettings {
  enabled: boolean;
  hour: number;
}

export interface DailySummaryPreview {
  day: string;
  sales: { origin: 'site' | 'counter' | 'mercadoLivre'; orders: number; total: number }[];
  staleParts: number;
  pendingQuestions: number;
}

const ORIGIN_LABELS: Record<string, string> = { site: 'Loja virtual', counter: 'Balcão', mercadoLivre: 'Mercado Livre' };
const MONEY = new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' });

/** Resumo diário por e-mail aos Donos (RF34). */
@Injectable({ providedIn: 'root' })
export class DailySummaryApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/painel/resumo-diario';

  get(): Promise<DailySummarySettings> {
    return firstValueFrom(this.http.get<DailySummarySettings>(this.base));
  }

  save(ativo: boolean, hora: number): Promise<DailySummarySettings> {
    return firstValueFrom(this.http.put<DailySummarySettings>(this.base, { ativo, hora }));
  }

  preview(): Promise<DailySummaryPreview> {
    return firstValueFrom(this.http.get<DailySummaryPreview>(`${this.base}/previa`));
  }
}

/**
 * Em Dados da loja: liga/desliga o resumo diário e escolhe a hora (padrão 7h, horário de Brasília). A prévia mostra os
 * números de ontem como vão no e-mail.
 */
@Component({
  selector: 'app-daily-summary-settings',
  imports: [FormsModule],
  template: `
    <section data-daily-summary>
      <h2>Resumo diário</h2>
      <p class="muted">
        Todo dia, os Donos da loja recebem por e-mail as vendas de ontem por canal, as perguntas de compradores sem resposta e
        as peças com estoque paradas há mais de 90 dias.
      </p>
      <label class="check">
        <input type="checkbox" [(ngModel)]="enabled" name="summary-enabled" data-enabled />
        Enviar o resumo diário
      </label>
      <label>
        Horário (Brasília)
        <select [(ngModel)]="hour" name="summary-hour" [disabled]="!enabled" data-hour>
          @for (h of hours; track h) {
            <option [ngValue]="h">{{ label(h) }}</option>
          }
        </select>
      </label>
      <div class="order-actions">
        <button type="button" (click)="save()" [disabled]="busy()" data-save>Salvar</button>
        <button type="button" class="link" (click)="loadPreview()" [disabled]="busy()" data-preview>Ver os números de ontem</button>
      </div>
      @if (saved()) {
        <p class="ok" role="status">Resumo diário salvo.</p>
      }
      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
      @if (preview(); as p) {
        <div data-preview-result>
          @for (s of p.sales; track s.origin) {
            <p>{{ originLabel(s.origin) }}: {{ s.orders }} {{ s.orders === 1 ? 'pedido' : 'pedidos' }}, {{ money(s.total) }}</p>
          } @empty {
            <p>Nenhuma venda paga ontem.</p>
          }
          <p>Perguntas sem resposta: {{ p.pendingQuestions }}</p>
          <p>Peças paradas há mais de 90 dias: {{ p.staleParts }}</p>
        </div>
      }
    </section>
  `,
})
export class DailySummarySettingsSection implements OnInit {
  private readonly api = inject(DailySummaryApi);

  protected readonly hours = Array.from({ length: 24 }, (_, i) => i);
  protected readonly busy = signal(false);
  protected readonly saved = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly preview = signal<DailySummaryPreview | null>(null);
  protected enabled = true;
  protected hour = 7;
  protected readonly label = (h: number) => `${String(h).padStart(2, '0')}:00`;
  protected readonly originLabel = (origin: string) => ORIGIN_LABELS[origin] ?? origin;
  protected readonly money = (value: number) => MONEY.format(value);

  async ngOnInit(): Promise<void> {
    try {
      const settings = await this.api.get();
      this.enabled = settings.enabled;
      this.hour = settings.hour;
    } catch (error) {
      this.error.set(problemTitle(error));
    }
  }

  async save(): Promise<void> {
    await this.run(async () => {
      const settings = await this.api.save(this.enabled, this.hour);
      this.enabled = settings.enabled;
      this.hour = settings.hour;
      this.saved.set(true);
    });
  }

  async loadPreview(): Promise<void> {
    await this.run(async () => this.preview.set(await this.api.preview()));
  }

  private async run(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.saved.set(false);
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
