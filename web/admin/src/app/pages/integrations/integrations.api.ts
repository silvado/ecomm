import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export interface IntegrationLogEntry {
  id: string;
  at: string;
  channel: string;
  operation: string;
  success: boolean;
  durationMs: number;
  errorSummary: string | null;
  reference: string | null;
  orderId: string | null;
  orderNumber: number | null;
  partId: string | null;
  correlationId: string | null;
}

export interface IntegrationLogPage {
  items: IntegrationLogEntry[];
  total: number;
  page: number;
  pageSize: number;
}

export interface IntegrationLogFilter {
  channel?: string;
  status?: '' | 'ok' | 'falha';
  orderId?: string;
  partId?: string;
  from?: string;
  to?: string;
  page?: number;
}

export interface MessageFailure {
  id: string;
  failedAt: string;
  description: string;
  attempts: number;
  errorType: string;
  errorSummary: string | null;
  status: 'pending' | 'replayed';
  replayedAt: string | null;
  replayedByEmail: string | null;
  correlationId: string | null;
}

export interface IntegrationSummary {
  pendingFailures: number;
  failedCallsLast24h: number;
}

export const CHANNEL_LABELS: Record<string, string> = {
  'mercado-pago': 'Mercado Pago',
  cielo: 'Cielo',
  'melhor-envio': 'Melhor Envio',
  fake: 'Simulado (desenvolvimento)',
};

/** Integrações da loja (RF28/RF29): log das chamadas externas, falhas em segundo plano e reprocessamento. */
@Injectable({ providedIn: 'root' })
export class IntegrationsApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/painel/integracoes';

  log(filter: IntegrationLogFilter, pageSize = 50): Promise<IntegrationLogPage> {
    let params = new HttpParams().set('pagina', filter.page ?? 1).set('tamanho', pageSize);
    if (filter.channel) params = params.set('canal', filter.channel);
    if (filter.status) params = params.set('situacao', filter.status);
    if (filter.orderId) params = params.set('pedido', filter.orderId);
    if (filter.partId) params = params.set('peca', filter.partId);
    if (filter.from) params = params.set('de', filter.from);
    if (filter.to) params = params.set('ate', filter.to);
    return firstValueFrom(this.http.get<IntegrationLogPage>(`${this.base}/log`, { params }));
  }

  failures(pendingOnly: boolean): Promise<MessageFailure[]> {
    return firstValueFrom(this.http.get<MessageFailure[]>(`${this.base}/falhas`, { params: { pendentes: pendingOnly } }));
  }

  summary(): Promise<IntegrationSummary> {
    return firstValueFrom(this.http.get<IntegrationSummary>(`${this.base}/resumo`));
  }

  replay(id: string): Promise<void> {
    return firstValueFrom(this.http.post<void>(`${this.base}/falhas/${id}/reprocessar`, {}));
  }
}
