import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { feedback } from '../../core/feedback';

export type GatewayName = 'mercado-pago' | 'cielo';

/** O que o painel vê de uma conta de pagamento: nunca as chaves, só os 4 últimos caracteres (RF06 CA3). */
export interface GatewaySettings {
  gateway: GatewayName;
  configured: boolean;
  /** Com os dois conectados, a loja cobra pelo salvo por último. */
  active: boolean;
  credentials: { field: string; hint: string }[];
  updatedAt: string | null;
  notificationUrl: string;
}

/** Contas de pagamento da loja atual (RF15) — só o Dono (permissão vaultManage). */
@Injectable({ providedIn: 'root' })
export class PaymentsApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/painel/pagamentos';

  list(): Promise<GatewaySettings[]> {
    return firstValueFrom(this.http.get<GatewaySettings[]>(this.base));
  }

  save(gateway: GatewayName, credentials: Record<string, string>): Promise<GatewaySettings[]> {
    return firstValueFrom(this.http.put<GatewaySettings[]>(`${this.base}/${gateway}`, credentials, { context: feedback(null) }));
  }

  remove(gateway: GatewayName): Promise<GatewaySettings[]> {
    return firstValueFrom(this.http.delete<GatewaySettings[]>(`${this.base}/${gateway}`, { context: feedback(null) }));
  }
}
