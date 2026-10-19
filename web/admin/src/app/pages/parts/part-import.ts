import { DOCUMENT } from '@angular/common';
import { Component, inject, OnDestroy, OnInit, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { problemTitle } from '../../core/auth';
import { PartImportApi, PartImportView } from './part-import.api';

const WHEN = new Intl.DateTimeFormat('pt-BR', { timeZone: 'America/Sao_Paulo', dateStyle: 'short', timeStyle: 'short' });
const POLL_MS = 1000;
const MAX_FILE_BYTES = 5 * 1024 * 1024;

/**
 * Importação de peças por planilha (RF11): baixa o modelo, envia o arquivo, confere a prévia (linhas boas, erros e
 * códigos já cadastrados) e confirma; o cadastro roda em segundo plano e a tela acompanha o progresso.
 */
@Component({
  selector: 'app-part-import',
  imports: [RouterLink],
  template: `
    <main class="card wide">
      <p><a routerLink="/pecas">← Peças</a></p>
      <h1>Importar peças por planilha</h1>

      <section>
        <h2>1. Baixe o modelo</h2>
        <p class="muted">
          Uma linha por peça. Código em branco (ou sem a coluna) é gerado no cadastro (P-000001…). Obrigatórias: título, estado (usada, nova ou recondicionada), preço e quantidade. Medidas da
          embalagem (cm e g) e códigos OEM (separados por |) são opcionais. As peças entram como rascunho: envie as fotos e ative
          depois. Códigos que já existem na loja são pulados, sem mudar a peça cadastrada.
        </p>
        <div class="order-actions">
          <button type="button" class="link" (click)="download('xlsx')" data-template="xlsx">Modelo em Excel (.xlsx)</button>
          <button type="button" class="link" (click)="download('csv')" data-template="csv">Modelo em CSV</button>
        </div>
      </section>

      <section>
        <h2>2. Envie a planilha</h2>
        <label class="button-like">
          {{ busy() ? 'Lendo a planilha…' : 'Escolher arquivo (.xlsx ou .csv)' }}
          <input type="file" accept=".xlsx,.csv,text/csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" (change)="upload($event)" [disabled]="busy() || running()" hidden />
        </label>
        <small>Até 5 MB e {{ maxRows }} peças por arquivo.</small>
        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }
      </section>

      @if (current(); as i) {
        <section aria-live="polite" data-import>
          <h2>{{ i.status === 'validated' ? '3. Confira e confirme' : i.status === 'processing' ? 'Importando…' : 'Importação concluída' }}</h2>
          <p>
            <strong>{{ i.fileName }}</strong>: {{ i.totalRows }} {{ i.totalRows === 1 ? 'linha' : 'linhas' }} ·
            <span data-valid>{{ i.validRows }} {{ i.validRows === 1 ? 'válida' : 'válidas' }}</span> ·
            <span data-errors>{{ i.errorCount }} com erro</span>
            @if (i.status === 'validated' && i.existingCodesAtPreview) {
              · {{ i.existingCodesAtPreview }} com código já cadastrado (serão puladas)
            }
          </p>

          @if (i.status === 'validated') {
            @if (i.validRows) {
              <p class="muted">As linhas com erro não serão importadas. Corrija-as na planilha e envie de novo depois, se quiser.</p>
              <div class="order-actions">
                <button type="button" (click)="confirm(i)" [disabled]="busy()" data-confirm>Importar {{ i.validRows }} {{ i.validRows === 1 ? 'peça' : 'peças' }}</button>
                <button type="button" class="link" (click)="current.set(null)" [disabled]="busy()">Cancelar</button>
              </div>
            } @else {
              <p class="warn">Nenhuma linha válida para importar. Corrija a planilha e envie de novo.</p>
            }
          } @else {
            <progress [value]="i.processedRows" [max]="i.validRows || 1" aria-label="Progresso da importação"></progress>
            <p data-progress>
              {{ i.processedRows }} de {{ i.validRows }} processadas · {{ i.importedCount }} {{ i.importedCount === 1 ? 'peça cadastrada' : 'peças cadastradas' }}
              @if (i.skippedCount) {
                · {{ i.skippedCount }} {{ i.skippedCount === 1 ? 'pulada' : 'puladas' }} (código já existia)
              }
            </p>
            @if (i.status === 'completed') {
              <p class="ok" role="status">
                Pronto. As peças estão em <a [routerLink]="['/pecas']">Peças</a> como rascunho: envie as fotos e ative.
              </p>
            }
          }

          @if (i.errors.length) {
            <h3>Linhas com erro</h3>
            <table data-error-list>
              <thead><tr><th class="num">Linha</th><th>Motivo</th></tr></thead>
              <tbody>
                @for (e of i.errors; track $index) {
                  <tr><td class="num">{{ e.line || '—' }}</td><td>{{ e.message }}</td></tr>
                }
              </tbody>
            </table>
            @if (i.errorCount > i.errors.length) {
              <p class="muted">Mostrando {{ i.errors.length }} de {{ i.errorCount }} erros.</p>
            }
          }
          @if (i.skipped.length) {
            <h3>Puladas (código já cadastrado)</h3>
            <p class="muted">{{ skippedText(i) }}</p>
          }
        </section>
      }

      @if (recent().length) {
        <h2>Importações recentes</h2>
        <table>
          <tbody>
            @for (r of recent(); track r.id) {
              <tr>
                <td>{{ when(r.createdAt) }}</td>
                <td><button type="button" class="link" (click)="open(r.id)">{{ r.fileName }}</button></td>
                <td>{{ statusLabels[r.status] }}</td>
                <td class="num">{{ r.importedCount }} cadastradas · {{ r.skippedCount }} puladas · {{ r.errorCount }} com erro</td>
              </tr>
            }
          </tbody>
        </table>
      }
    </main>
  `,
})
export class PartImportPage implements OnInit, OnDestroy {
  private readonly api = inject(PartImportApi);
  private readonly document = inject(DOCUMENT);
  private timer?: ReturnType<typeof setTimeout>;

  protected readonly maxRows = 5000;
  protected readonly statusLabels: Record<PartImportView['status'], string> = {
    validated: 'Não confirmada',
    processing: 'Importando',
    completed: 'Concluída',
  };
  protected readonly current = signal<PartImportView | null>(null);
  protected readonly recent = signal<PartImportView[]>([]);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly when = (iso: string) => WHEN.format(new Date(iso));
  protected readonly running = () => this.current()?.status === 'processing';

  async ngOnInit(): Promise<void> {
    await this.loadRecent();
  }

  ngOnDestroy(): void {
    clearTimeout(this.timer);
  }

  protected skippedText(i: PartImportView): string {
    const shown = i.skipped.slice(0, 50).map((s) => `${s.internalCode} (linha ${s.line})`);
    return shown.join(', ') + (i.skipped.length > 50 ? ` e mais ${i.skipped.length - 50}.` : '.');
  }

  protected async download(format: 'xlsx' | 'csv'): Promise<void> {
    this.error.set(null);
    try {
      const url = URL.createObjectURL(await this.api.template(format));
      const link = this.document.createElement('a');
      link.href = url;
      link.download = `modelo-importacao-pecas.${format}`;
      link.click();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (error) {
      this.error.set(problemTitle(error));
    }
  }

  protected async upload(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    this.error.set(null);
    if (file.size > MAX_FILE_BYTES) {
      this.error.set('A planilha pode ter no máximo 5 MB.');
      return;
    }
    this.busy.set(true);
    try {
      this.current.set(await this.api.preview(file));
    } catch (error) {
      this.current.set(null);
      this.error.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }

  protected async confirm(i: PartImportView): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      this.track(await this.api.confirm(i.id));
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }

  protected async open(id: string): Promise<void> {
    try {
      this.track(await this.api.get(id));
    } catch (error) {
      this.error.set(problemTitle(error));
    }
  }

  /** Mostra a importação e, enquanto estiver rodando, consulta o progresso a cada segundo. */
  private track(view: PartImportView): void {
    clearTimeout(this.timer);
    this.current.set(view);
    if (view.status === 'processing') {
      this.timer = setTimeout(() => void this.refresh(view.id), POLL_MS);
    } else if (view.status === 'completed') {
      void this.loadRecent();
    }
  }

  private async refresh(id: string): Promise<void> {
    if (this.current()?.id !== id) return;
    try {
      this.track(await this.api.get(id));
    } catch (error) {
      this.error.set(problemTitle(error));
    }
  }

  private async loadRecent(): Promise<void> {
    try {
      this.recent.set(await this.api.recent());
    } catch {
      // A lista é só um atalho; a importação em si continua funcionando.
    }
  }
}
