import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, DestroyRef, inject, OnInit, output, signal } from '@angular/core';
import { problemTitle } from '../../core/auth';
import { DataExport, PrivacyApi } from './privacy.api';

/** Enquanto houver exportação na fila, confere de tempos em tempos. */
const POLL_MS = 3000;

const STATUS: Record<DataExport['status'], string> = {
  pending: 'Gerando…',
  ready: 'Pronta',
  failed: 'Falhou',
  expired: 'Vencida',
};

/**
 * Exportação completa da loja (RF39): o Dono pede, a plataforma monta o pacote (ZIP) em segundo plano e o download vale
 * por 7 dias. Pedir e baixar ficam no registro de acesso.
 */
@Component({
  selector: 'app-export-section',
  imports: [DatePipe, DecimalPipe],
  template: `
    <section data-exports>
      <h2>Exportar dados da loja</h2>
      <p class="muted">
        Gera um pacote (ZIP) com peças, fotos, pedidos, clientes, notas fiscais e configurações, em planilhas que abrem no Excel.
        Senhas, chaves de pagamento e o certificado digital não vão junto. O arquivo pode ser baixado por 7 dias e contém dados pessoais
        de compradores: guarde em local seguro.
      </p>
      <button type="button" (click)="request()" [disabled]="busy() || hasPending()" data-request-export>
        {{ hasPending() ? 'Gerando exportação…' : 'Gerar exportação' }}
      </button>
      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
      @if (exports().length) {
        <table>
          <thead><tr><th>Pedida em</th><th>Por</th><th>Situação</th><th class="num">Tamanho</th><th>Vale até</th><th></th></tr></thead>
          <tbody>
            @for (e of exports(); track e.id) {
              <tr>
                <td>{{ e.requestedAt | date: 'dd/MM/yyyy HH:mm' }}</td>
                <td>{{ e.requestedByEmail }}</td>
                <td [attr.title]="e.error">{{ status[e.status] }}</td>
                <td class="num">@if (e.sizeBytes) { {{ e.sizeBytes / 1048576 | number: '1.1-1' : 'pt-BR' }} MB }</td>
                <td>{{ e.expiresAt | date: 'dd/MM/yyyy HH:mm' }}</td>
                <td>
                  @if (e.canDownload) {
                    <button type="button" class="link" (click)="download(e)" [disabled]="busy()" data-download>Baixar</button>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      }
    </section>
  `,
})
export class ExportSection implements OnInit {
  private readonly api = inject(PrivacyApi);
  private timer: ReturnType<typeof setTimeout> | null = null;

  /** Pedir e baixar entram no registro de acesso: a tela recarrega a lista. */
  readonly changed = output<void>();
  protected readonly status = STATUS;
  protected readonly exports = signal<DataExport[]>([]);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly hasPending = () => this.exports().some((e) => e.status === 'pending');

  constructor() {
    inject(DestroyRef).onDestroy(() => this.timer && clearTimeout(this.timer));
  }

  ngOnInit(): void {
    void this.load();
  }

  async request(): Promise<void> {
    await this.run(async () => {
      await this.api.requestExport();
      this.changed.emit();
      await this.load();
    });
  }

  async download(item: DataExport): Promise<void> {
    await this.run(async () => {
      const blob = await this.api.downloadExport(item.id);
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `exportacao-${item.completedAt?.slice(0, 10) ?? item.id}.zip`;
      link.click();
      URL.revokeObjectURL(url);
      this.changed.emit();
    });
  }

  private async load(): Promise<void> {
    if (this.timer) clearTimeout(this.timer);
    try {
      this.exports.set(await this.api.exports());
      if (this.hasPending()) this.timer = setTimeout(() => void this.load(), POLL_MS);
    } catch (error) {
      this.error.set(problemTitle(error));
    }
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
