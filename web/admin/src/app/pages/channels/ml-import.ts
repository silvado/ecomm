import { HttpClient } from '@angular/common/http';
import { Component, computed, DestroyRef, inject, Injectable, OnInit, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { problemTitle } from '../../core/auth';

export interface ChannelImport {
  id: string;
  status: 'listing' | 'processing' | 'completed' | 'failed';
  createdAt: string;
  finishedAt: string | null;
  totalItems: number;
  truncated: boolean;
  processedItems: number;
  createdCount: number;
  linkedCount: number;
  alreadyLinkedCount: number;
  rejectedCount: number;
  compatibilitiesCount: number;
  issues: { externalId: string; message: string }[];
  failureReason: string | null;
}

/** Importação dos anúncios que a loja já tem no Mercado Livre (RF11). */
@Injectable({ providedIn: 'root' })
export class MercadoLivreImportApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/painel/canais/mercado-livre/importacoes';

  start(): Promise<ChannelImport> {
    return firstValueFrom(this.http.post<ChannelImport>(this.base, {}));
  }

  get(id: string): Promise<ChannelImport> {
    return firstValueFrom(this.http.get<ChannelImport>(`${this.base}/${id}`));
  }

  recent(): Promise<ChannelImport[]> {
    return firstValueFrom(this.http.get<ChannelImport[]>(this.base));
  }
}

const POLL_MS = 2000;

/**
 * Em Canais de venda, com a conta do ML conectada: traz os anúncios ativos e pausados como peças vinculadas, acompanha o
 * progresso e mostra o relatório (o que foi ligado a peças existentes e o que não entrou).
 */
@Component({
  selector: 'app-ml-import',
  template: `
    <div data-ml-import>
      <h3>Importar anúncios do Mercado Livre</h3>
      <p class="muted">
        Traz os anúncios ativos e pausados desta conta como peças da loja, com fotos, já vinculadas ao anúncio. Anúncio com o mesmo
        código (SKU) de uma peça que você já tem é ligado a ela, e vale o estoque da loja. Os veículos compatíveis do anúncio entram nas peças
        novas quando existem na tabela de veículos. Importar de novo não duplica.
      </p>
      @if (current(); as i) {
        @if (running()) {
          <p role="status" data-progress>
            @if (i.status === 'listing') {
              Lendo a lista de anúncios… {{ i.totalItems }} até agora.
            } @else {
              Importando: {{ i.processedItems }} de {{ i.totalItems }}.
            }
          </p>
        } @else if (i.status === 'failed') {
          <p class="error" role="alert">A importação parou: {{ i.failureReason }}</p>
        } @else {
          <p class="ok" role="status" data-result>
            Importação concluída: {{ i.createdCount }} peças novas, {{ i.linkedCount }} ligadas a peças existentes,
            {{ i.alreadyLinkedCount }} já vinculadas e {{ i.rejectedCount }} não importadas.
            @if (i.compatibilitiesCount) {
              {{ i.compatibilitiesCount }} compatibilidades com veículos trazidas dos anúncios.
            }
          </p>
        }
        @if (i.truncated) {
          <p class="warn">A conta tem mais de 5.000 anúncios: só os primeiros 5.000 foram importados.</p>
        }
        @if (i.issues.length) {
          <table data-issues>
            <thead><tr><th>Anúncio</th><th>O que aconteceu</th></tr></thead>
            <tbody>
              @for (issue of i.issues; track $index) {
                <tr><td>{{ issue.externalId }}</td><td>{{ issue.message }}</td></tr>
              }
            </tbody>
          </table>
        }
      }
      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
      <div class="order-actions">
        <button type="button" (click)="start()" [disabled]="busy() || running()" data-start-import>
          {{ current() ? 'Importar de novo' : 'Importar anúncios' }}
        </button>
      </div>
    </div>
  `,
})
export class MercadoLivreImportSection implements OnInit {
  private readonly api = inject(MercadoLivreImportApi);
  private timer: ReturnType<typeof setTimeout> | null = null;

  protected readonly current = signal<ChannelImport | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly running = computed(() => ['listing', 'processing'].includes(this.current()?.status ?? ''));

  constructor() {
    inject(DestroyRef).onDestroy(() => this.stop());
  }

  async ngOnInit(): Promise<void> {
    try {
      const [last] = await this.api.recent();
      this.track(last ?? null);
    } catch (error) {
      this.error.set(problemTitle(error));
    }
  }

  async start(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      this.track(await this.api.start());
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }

  /** Mostra a importação e, enquanto roda, consulta o progresso. */
  private track(item: ChannelImport | null): void {
    this.stop();
    this.current.set(item);
    if (item && this.running()) this.timer = setTimeout(() => void this.poll(item.id), POLL_MS);
  }

  private async poll(id: string): Promise<void> {
    try {
      this.track(await this.api.get(id));
    } catch (error) {
      this.error.set(problemTitle(error));
    }
  }

  private stop(): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
  }
}
