import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';

/** O que o painel vê da conta do Mercado Pago: nunca as chaves, só os 4 últimos caracteres (RF06 CA3). */
export interface MercadoPagoSettings {
  configured: boolean;
  accessTokenHint: string | null;
  webhookSecretHint: string | null;
  updatedAt: string | null;
  notificationUrl: string;
}

/** Credenciais de pagamento da loja atual (RF15) — só o Dono (permissão vaultManage). */
@Injectable({ providedIn: 'root' })
export class PaymentsApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/painel/pagamentos/mercado-pago';

  get(): Promise<MercadoPagoSettings> {
    return firstValueFrom(this.http.get<MercadoPagoSettings>(this.base));
  }

  save(accessToken: string, webhookSecret: string): Promise<MercadoPagoSettings> {
    return firstValueFrom(this.http.put<MercadoPagoSettings>(this.base, { accessToken, webhookSecret }));
  }

  remove(): Promise<MercadoPagoSettings> {
    return firstValueFrom(this.http.delete<MercadoPagoSettings>(this.base));
  }
}
