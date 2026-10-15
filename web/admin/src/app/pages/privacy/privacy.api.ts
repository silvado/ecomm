import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';

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
    return firstValueFrom(this.http.post<AnonymizationPreview>(`${this.base}/titular/previa`, subject));
  }

  anonymize(subject: DataSubject): Promise<AnonymizationPreview> {
    return firstValueFrom(this.http.post<AnonymizationPreview>(`${this.base}/titular/anonimizar`, subject));
  }

  accesses(acao: string, pagina: number): Promise<AccessPage> {
    let params = new HttpParams().set('pagina', pagina);
    if (acao) params = params.set('acao', acao);
    return firstValueFrom(this.http.get<AccessPage>(`${this.base}/acessos`, { params }));
  }
}
