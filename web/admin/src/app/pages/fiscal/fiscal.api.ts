import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { PartView } from '../parts/parts.api';

export type FiscalMode = 'disabled' | 'provider' | 'external';
export type FiscalEnvironment = 'homologation' | 'production';
export type TaxRegime = 'simplesNacional' | 'normal';

export interface FiscalSettings {
  mode: FiscalMode;
  environment: FiscalEnvironment;
  regime: TaxRegime;
  state: string | null;
  defaultNcm: string | null;
  cfopInState: string | null;
  cfopOutOfState: string | null;
  defaultTaxCode: string | null;
  defaultOrigin: number | null;
}

export interface FiscalSettingsView {
  settings: FiscalSettings;
  issuerCnpj: string;
  providerConnected: boolean;
}

export interface PartFiscal {
  ncm: string | null;
  taxCode: string | null;
  origin: number | null;
}

export interface RecipientAddress {
  postalCode: string;
  street: string;
  number: string;
  complement: string | null;
  district: string;
  city: string;
  state: string;
}

export interface FiscalRecipient {
  name: string;
  document: string;
  email: string | null;
  address: RecipientAddress | null;
}

export type FiscalStatus = 'processing' | 'authorized' | 'rejected' | 'canceled';

export interface FiscalDocument {
  id: string;
  source: 'provider' | 'external';
  status: FiscalStatus;
  environment: FiscalEnvironment | null;
  accessKey: string | null;
  number: number | null;
  series: number | null;
  message: string | null;
  requestedAt: string;
  authorizedAt: string | null;
  canceledAt: string | null;
  cancelJustification: string | null;
  hasXml: boolean;
  hasDanfe: boolean;
  canCancel: boolean;
  cancelDeadline: string | null;
}

export interface InvoiceDraft {
  mode: FiscalMode;
  recipient: FiscalRecipient | null;
  problems: string[];
  documents: FiscalDocument[];
}

/** NF-e (RF19): configuração (Dono), dados da peça (catálogo) e emissão no pedido (Dono e Operador). */
@Injectable({ providedIn: 'root' })
export class FiscalApi {
  private readonly http = inject(HttpClient);

  settings(): Promise<FiscalSettingsView> {
    return firstValueFrom(this.http.get<FiscalSettingsView>('/api/painel/loja/fiscal'));
  }

  saveSettings(settings: FiscalSettings): Promise<FiscalSettingsView> {
    return firstValueFrom(this.http.put<FiscalSettingsView>('/api/painel/loja/fiscal', settings));
  }

  setPartFiscal(partId: string, fiscal: PartFiscal): Promise<PartView> {
    return firstValueFrom(this.http.put<PartView>(`/api/painel/pecas/${partId}/fiscal`, fiscal));
  }

  draft(orderId: string): Promise<InvoiceDraft> {
    return firstValueFrom(this.http.get<InvoiceDraft>(`/api/painel/pedidos/${orderId}/nfe`));
  }

  emit(orderId: string, recipient: FiscalRecipient): Promise<InvoiceDraft> {
    return firstValueFrom(this.http.post<InvoiceDraft>(`/api/painel/pedidos/${orderId}/nfe`, { recipient }));
  }

  registerExternal(orderId: string, accessKey: string | null, xmlBase64: string | null): Promise<InvoiceDraft> {
    return firstValueFrom(this.http.post<InvoiceDraft>(`/api/painel/pedidos/${orderId}/nfe/externa`, { accessKey, xmlBase64 }));
  }

  cancel(orderId: string, documentId: string, justification: string): Promise<InvoiceDraft> {
    return firstValueFrom(this.http.post<InvoiceDraft>(`/api/painel/pedidos/${orderId}/nfe/${documentId}/cancelar`, { justification }));
  }

  /** Arquivo pelo HttpClient (o token vai no cabeçalho); o navegador salva com o nome da nota. */
  async download(orderId: string, documentId: string, file: 'xml' | 'danfe'): Promise<Blob> {
    return firstValueFrom(this.http.get(`/api/painel/pedidos/${orderId}/nfe/${documentId}/${file}`, { responseType: 'blob' }));
  }
}

export const UF = ['AC', 'AL', 'AP', 'AM', 'BA', 'CE', 'DF', 'ES', 'GO', 'MA', 'MT', 'MS', 'MG', 'PA', 'PB', 'PR', 'PE', 'PI', 'RJ', 'RN', 'RS', 'RO', 'RR', 'SC', 'SP', 'SE', 'TO'];

export const FISCAL_STATUS: Record<FiscalStatus, string> = {
  processing: 'Processando na SEFAZ',
  authorized: 'Autorizada',
  rejected: 'Rejeitada',
  canceled: 'Cancelada',
};
