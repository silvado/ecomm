import { HttpClient, HttpResponse } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export type SecretKind = 'paymentGatewayCredential' | 'a1Certificate' | 'a1CertificatePassword' | 'oAuthToken' | 'apiKey';

export interface SecretMetadata {
  kind: SecretKind;
  name: string;
  hint: string;
  expiresAt: string | null;
  updatedAt: string;
}

export interface CertificateView {
  holder: string;
  cnpj: string | null;
  notBefore: string;
  expiresAt: string;
  daysLeft: number;
  updatedAt: string;
}

export type CertificateAlertLevel = 'notice' | 'warning' | 'urgent' | 'expired';

export interface CertificateAlert {
  expiresAt: string;
  daysLeft: number;
  level: CertificateAlertLevel;
}

/** Cofre (RF06), só o Dono: metadados dos segredos e o certificado A1, que entra mas nunca sai. */
@Injectable({ providedIn: 'root' })
export class VaultApi {
  private readonly http = inject(HttpClient);

  secrets(): Promise<SecretMetadata[]> {
    return firstValueFrom(this.http.get<SecretMetadata[]>('/api/painel/cofre'));
  }

  async certificate(): Promise<CertificateView | null> {
    const response = await firstValueFrom(this.http.get<CertificateView>('/api/painel/cofre/certificado', { observe: 'response' }));
    return response.status === 204 ? null : response.body;
  }

  saveCertificate(pfxBase64: string, password: string): Promise<CertificateView> {
    return firstValueFrom(this.http.put<CertificateView>('/api/painel/cofre/certificado', { pfxBase64, password }));
  }

  removeCertificate(): Promise<unknown> {
    return firstValueFrom(this.http.delete('/api/painel/cofre/certificado'));
  }

  async alert(): Promise<CertificateAlert | null> {
    const response: HttpResponse<CertificateAlert> = await firstValueFrom(
      this.http.get<CertificateAlert>('/api/painel/cofre/alerta', { observe: 'response' }),
    );
    return response.status === 204 ? null : response.body;
  }
}

export const KIND_LABELS: Record<SecretKind, string> = {
  paymentGatewayCredential: 'Pagamento',
  a1Certificate: 'Certificado digital A1',
  a1CertificatePassword: 'Senha do certificado A1',
  oAuthToken: 'Acesso a canal de venda',
  apiKey: 'Chave de serviço',
};

/** Nomes conhecidos em linguagem do lojista; os demais aparecem como estão. */
export const NAME_LABELS: Record<string, string> = {
  'mercado-pago:access-token': 'Mercado Pago — Access Token',
  'mercado-pago:webhook-secret': 'Mercado Pago — assinatura das notificações',
  'cielo:merchant-id': 'Cielo — MerchantId',
  'cielo:client-id': 'Cielo — Client ID',
  'cielo:client-secret': 'Cielo — Client Secret',
  'smtp:password': 'E-mail da loja — senha do SMTP',
  'a1:certificado': 'Arquivo do certificado',
  'a1:senha': 'Senha do certificado',
};

/** Texto da faixa de aviso do certificado (RF06 CA5). */
export function alertText(alert: CertificateAlert): string {
  const date = new Intl.DateTimeFormat('pt-BR', { timeZone: 'America/Sao_Paulo', dateStyle: 'short' }).format(new Date(alert.expiresAt));
  if (alert.level === 'expired') return `O certificado digital A1 venceu em ${date}. A NF-e pela plataforma não pode ser emitida até enviar o certificado renovado.`;
  const when = alert.daysLeft <= 0 ? 'vence hoje' : alert.daysLeft === 1 ? 'vence amanhã' : `vence em ${alert.daysLeft} dias`;
  return `O certificado digital A1 ${when} (${date}). Renove com a certificadora e envie o novo no Cofre.`;
}
