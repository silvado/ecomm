import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { feedback } from '../../core/feedback';

export interface DataSubject {
  email?: string;
  documento?: string;
}

export interface AnonymizationPreview {
  orders: number;
  inProgress: number[];
  withInvoice: number;
  alreadyAnonymized: number;
}

export interface AccessItem {
  at: string;
  userEmail: string;
  action: string;
  orderId: string | null;
  orderNumber: number | null;
  details: string | null;
  ip: string | null;
}

export type ExportStatus = 'pending' | 'ready' | 'failed' | 'expired';

export interface DataExport {
  id: string;
  status: ExportStatus;
  requestedByEmail: string;
  requestedAt: string;
  completedAt: string | null;
  expiresAt: string | null;
  sizeBytes: number | null;
  error: string | null;
  canDownload: boolean;
}

export interface AccessPage {
  items: AccessItem[];
  page: number;
  pageSize: number;
  hasMore: boolean;
}

/** Mesmos textos de PersonalDataActions na API. */
export const ACCESS_LABELS: Record<string, string> = {
  'pedido.aberto': 'Abriu o pedido',
  'nfe.baixada': 'Baixou a nota fiscal',
  'titular.anonimizado': 'Anonimizou comprador',
  'exportacao.gerada': 'Gerou exportação',
  'exportacao.baixada': 'Baixou exportação',
};

/** Privacidade (RNF04): só o Dono. */
@Injectable({ providedIn: 'root' })
export class PrivacyApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/painel/privacidade';

  preview(subject: DataSubject): Promise<AnonymizationPreview> {
    return firstValueFrom(this.http.post<AnonymizationPreview>(`${this.base}/titular/previa`, subject, { context: feedback(null) }));
  }

  anonymize(subject: DataSubject): Promise<AnonymizationPreview> {
    return firstValueFrom(this.http.post<AnonymizationPreview>(`${this.base}/titular/anonimizar`, subject, { context: feedback(null) }));
  }

  exports(): Promise<DataExport[]> {
    return firstValueFrom(this.http.get<DataExport[]>(`${this.base}/exportacoes`));
  }

  requestExport(): Promise<DataExport> {
    return firstValueFrom(this.http.post<DataExport>(`${this.base}/exportacoes`, null, { context: feedback('Exportação iniciada.') }));
  }

  /** O arquivo vem pelo painel (com o token): o download fica registrado. */
  downloadExport(id: string): Promise<Blob> {
    return firstValueFrom(this.http.get(`${this.base}/exportacoes/${id}/arquivo`, { responseType: 'blob' }));
  }

  accesses(acao: string, pagina: number): Promise<AccessPage> {
    let params = new HttpParams().set('pagina', pagina);
    if (acao) params = params.set('acao', acao);
    return firstValueFrom(this.http.get<AccessPage>(`${this.base}/acessos`, { params }));
  }
}
