import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { SubscriptionInvoice } from '../../shared/invoice';

export interface SubscriptionView {
  planCode: string;
  planName: string;
  monthlyPrice: number;
  startedOn: string | null;
  dueDay: number | null;
  onlinePayment: boolean;
  invoices: SubscriptionInvoice[];
}

export type DunningStage = 'none' | 'warned' | 'readOnly' | 'suspended' | 'closing';

/** Situação da loja na régua de inadimplência (RF38): a faixa do painel, para todos os perfis. */
export interface BillingStanding {
  status: 'onboarding' | 'active' | 'readOnly' | 'suspended' | 'closing' | 'closed';
  stage: DunningStage;
  overdueSince: string | null;
  readOnly: boolean;
}

@Injectable({ providedIn: 'root' })
export class SubscriptionApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/painel/assinatura';

  get(): Promise<SubscriptionView> {
    return firstValueFrom(this.http.get<SubscriptionView>(this.base));
  }

  standing(): Promise<BillingStanding> {
    return firstValueFrom(this.http.get<BillingStanding>(`${this.base}/situacao`));
  }

  /** Página de pagamento do gateway da plataforma para a fatura. */
  pay(invoiceId: string): Promise<{ redirectUrl: string }> {
    return firstValueFrom(this.http.post<{ redirectUrl: string }>(`${this.base}/faturas/${invoiceId}/pagar`, null));
  }
}

/** Texto da faixa; nulo = em dia (ou atraso de menos de 3 dias). */
export function standingText(s: BillingStanding): string | null {
  const since = s.overdueSince ? s.overdueSince.split('-').reverse().join('/') : '';
  if (s.status === 'suspended')
    return s.stage === 'none' || s.stage === 'warned'
      ? 'Loja suspensa pela equipe da plataforma: o painel está somente para consulta.'
      : `Loja suspensa por fatura vencida em ${since}: a loja virtual e os anúncios estão fora do ar.`;
  if (s.status === 'closing') return `Loja em encerramento por fatura vencida em ${since}. Fale com a equipe da plataforma.`;
  if (s.readOnly) return `Painel somente leitura: fatura vencida em ${since}. A loja continua vendendo; pague para voltar a alterar.`;
  if (s.stage !== 'none') return `Fatura vencida em ${since}. Pague para evitar o bloqueio do painel.`;
  return null;
}
