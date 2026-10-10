import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export interface StoreShipping {
  originPostalCode: string | null;
  pickupEnabled: boolean;
  pickupAddress: string | null;
}

export type ContrastWarning = 'textOnBackground' | 'primaryOnBackground';

export interface StoreProfile {
  slug: string;
  cnpj: string;
  legalName: string;
  tradeName: string;
  theme: { primaryColor: string; onPrimaryColor: string; backgroundColor: string; textColor: string };
  texts: { about: string; returnPolicy: string; footer: string };
  warnings: ContrastWarning[];
  logoId: string | null;
  hideOutOfStock: boolean;
  shipping: StoreShipping;
}

export interface StoreProfileUpdate {
  tradeName: string;
  primaryColor: string;
  backgroundColor: string;
  textColor: string;
  about: string;
  returnPolicy: string;
  footer: string;
  hideOutOfStock: boolean;
  shipping: StoreShipping;
}

/** Dados da loja atual (a loja vem do token; a API só aceita quem tem a permissão storeManage). */
@Injectable({ providedIn: 'root' })
export class StoreApi {
  private readonly http = inject(HttpClient);

  get(): Promise<StoreProfile> {
    return firstValueFrom(this.http.get<StoreProfile>('/api/painel/loja'));
  }

  update(update: StoreProfileUpdate): Promise<StoreProfile> {
    return firstValueFrom(this.http.put<StoreProfile>('/api/painel/loja', update));
  }

  /** Envia os bytes da imagem como corpo (a API detecta o formato pelo conteúdo). */
  uploadLogo(file: Blob): Promise<StoreProfile> {
    return firstValueFrom(this.http.put<StoreProfile>('/api/painel/loja/logo', file));
  }

  removeLogo(): Promise<StoreProfile> {
    return firstValueFrom(this.http.delete<StoreProfile>('/api/painel/loja/logo'));
  }

  /** Prévia do logo atual (a URL pública só responde no domínio da loja). */
  logo(): Promise<Blob> {
    return firstValueFrom(this.http.get('/api/painel/loja/logo', { responseType: 'blob' }));
  }
}
