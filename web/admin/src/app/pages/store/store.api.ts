import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export type ContrastWarning = 'textOnBackground' | 'primaryOnBackground';

export interface StoreProfile {
  slug: string;
  cnpj: string;
  legalName: string;
  tradeName: string;
  theme: { primaryColor: string; onPrimaryColor: string; backgroundColor: string; textColor: string };
  texts: { about: string; returnPolicy: string; footer: string };
  warnings: ContrastWarning[];
}

export interface StoreProfileUpdate {
  tradeName: string;
  primaryColor: string;
  backgroundColor: string;
  textColor: string;
  about: string;
  returnPolicy: string;
  footer: string;
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
}
