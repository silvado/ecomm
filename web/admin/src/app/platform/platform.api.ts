import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { SubscriptionInvoice } from '../shared/invoice';
import { feedback, silent } from '../core/feedback';

export type TenantStatus = 'Onboarding' | 'Active' | 'ReadOnly' | 'Suspended' | 'Closing' | 'Closed';

export interface PlatformTenantItem {
  id: string;
  slug: string;
  tradeName: string;
  cnpj: string;
  status: TenantStatus;
  planCode: string;
  primaryHost: string;
  customStorefront: boolean;
  createdAt: string;
}

export interface PlatformTenantDetail {
  id: string;
  slug: string;
  tradeName: string;
  legalName: string;
  cnpj: string;
  status: TenantStatus;
  planCode: string;
  planName: string;
  createdAt: string;
  customStorefrontSince: string | null;
  domains: { host: string; kind: string; status: string; isPrimary: boolean }[];
  users: { email: string; role: string; mustChangePassword: boolean; lockedOut: boolean }[];
  /** Situação posta pela régua de inadimplência (RF38): volta sozinha com o pagamento. */
  billingHold: boolean;
}

export interface NewTenant {
  slug: string;
  cnpj: string;
  razaoSocial: string;
  nome: string;
  dono: string;
}

export interface CreatedTenant {
  id: string;
  slug: string;
  host: string;
  ownerEmail: string;
  temporaryPassword: string | null;
}

export interface PlatformPlan {
  code: string;
  name: string;
  limits: Record<string, number>;
  features: string[];
  tenants: number;
  /** Mensalidade (RF38); zero = sem cobrança. */
  monthlyPrice: number;
}

export interface PlatformBilling {
  tenantId: string;
  planName: string;
  monthlyPrice: number;
  startedOn: string | null;
  dueDay: number | null;
  invoices: SubscriptionInvoice[];
}

export interface AuditItem {
  at: string;
  adminEmail: string;
  action: string;
  tenantId: string | null;
  tenantSlug: string | null;
  details: string | null;
  ip: string | null;
}

export interface AuditPage {
  items: AuditItem[];
  page: number;
  pageSize: number;
  hasMore: boolean;
}

export const STATUS_LABELS: Record<TenantStatus, string> = {
  Onboarding: 'Em implantação',
  Active: 'Ativa',
  ReadOnly: 'Somente leitura',
  Suspended: 'Suspensa',
  Closing: 'Em encerramento',
  Closed: 'Encerrada',
};

/** Ações da auditoria (mesmos textos de PlatformAuditActions na API). */
export const AUDIT_LABELS: Record<string, string> = {
  'superadmin.criado': 'Superadmin criado',
  login: 'Entrou',
  'login.falhou': 'Login recusado',
  'mfa.falhou': 'Código recusado',
  'mfa.cadastrado': 'App autenticador cadastrado',
  'mfa.codigo-de-recuperacao': 'Usou código de recuperação',
  'senha.trocada': 'Trocou a senha',
  'loja.visualizada': 'Abriu a loja',
  'loja.criada': 'Criou a loja',
  'loja.plano': 'Trocou o plano',
  'loja.suspensa': 'Suspendeu a loja',
  'loja.reativada': 'Reativou a loja',
  'loja.personalizada': 'Loja personalizada',
  'loja.tema': 'Salvou o tema exclusivo',
  'loja.pagina': 'Salvou uma página exclusiva',
  'loja.pagina.removida': 'Removeu uma página exclusiva',
  'cobranca.implantacao': 'Lançou a taxa de implantação',
  'cobranca.pagamento-manual': 'Registrou um pagamento',
  'cobranca.cancelada': 'Cancelou uma fatura',
  'plano.preco': 'Mudou a mensalidade de um plano',
};

/** Tema exclusivo da loja personalizada (RF40), já limpo pelo servidor. */
export interface ThemePageItem {
  id: string;
  slug: string;
  title: string;
  html: string;
  published: boolean;
  position: number;
  updatedAt: string;
  updatedBy: string;
}

export interface PlatformTheme {
  tenantId: string;
  tradeName: string;
  customStorefront: boolean;
  css: string;
  headerHtml: string;
  footerHtml: string;
  updatedAt: string | null;
  updatedBy: string | null;
  pages: ThemePageItem[];
}

export interface ThemeDraft {
  css: string;
  cabecalho: string;
  rodape: string;
}

export interface PageDraft {
  endereco: string;
  titulo: string;
  conteudo: string;
  publicada: boolean;
  ordem: number;
}

export interface ThemePreview {
  css: string;
  headerHtml: string;
  footerHtml: string;
  removed: string[];
}

/** Depois de salvar: o tema atualizado e o que a limpeza tirou. */
export interface ThemeSaved {
  theme: PlatformTheme;
  removed: string[];
}

export const LIMIT_LABELS: Record<string, string> = {
  users: 'Usuários',
  active_parts: 'Peças ativas',
  ai_listings_month: 'Cadastros por IA no mês',
};

export const FEATURE_LABELS: Record<string, string> = {
  'channel:mercado-livre': 'Mercado Livre',
  'channel:meta': 'Instagram/Facebook',
  'channel:olx': 'OLX',
  custom_domain: 'Domínio próprio',
  'ai:part_from_photos': 'IA: cadastro por foto',
  'ai:buyer_answers': 'IA: respostas a compradores',
  'ai:price_suggestion': 'IA: preço sugerido',
  'ai:daily_summary': 'IA: resumo diário',
};

/** Painel da plataforma (superadmin). Toda abertura de loja e toda ação ficam na auditoria (a API registra). */
@Injectable({ providedIn: 'root' })
export class PlatformApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/plataforma';

  tenants(search = '', status = ''): Promise<PlatformTenantItem[]> {
    let params = new HttpParams();
    if (search.trim()) params = params.set('busca', search.trim());
    if (status) params = params.set('status', status);
    return firstValueFrom(this.http.get<PlatformTenantItem[]>(`${this.base}/lojas`, { params }));
  }

  tenant(id: string): Promise<PlatformTenantDetail> {
    return firstValueFrom(this.http.get<PlatformTenantDetail>(`${this.base}/lojas/${id}`));
  }

  create(tenant: NewTenant): Promise<CreatedTenant> {
    return firstValueFrom(this.http.post<CreatedTenant>(`${this.base}/lojas`, tenant, { context: feedback('Loja criada.') }));
  }

  changePlan(id: string, plano: string): Promise<PlatformTenantDetail> {
    return firstValueFrom(this.http.put<PlatformTenantDetail>(`${this.base}/lojas/${id}/plano`, { plano }, { context: feedback('Plano alterado.') }));
  }

  suspend(id: string): Promise<PlatformTenantDetail> {
    return firstValueFrom(this.http.post<PlatformTenantDetail>(`${this.base}/lojas/${id}/suspender`, null, { context: feedback('Loja suspensa.') }));
  }

  reactivate(id: string): Promise<PlatformTenantDetail> {
    return firstValueFrom(this.http.post<PlatformTenantDetail>(`${this.base}/lojas/${id}/reativar`, null, { context: feedback('Loja reativada.') }));
  }

  setCustomStorefront(id: string, ligada: boolean): Promise<PlatformTenantDetail> {
    return firstValueFrom(this.http.put<PlatformTenantDetail>(`${this.base}/lojas/${id}/loja-personalizada`, { ligada }, { context: feedback(ligada ? 'Loja personalizada ligada.' : 'Loja personalizada desligada.') }));
  }

  theme(id: string): Promise<PlatformTheme> {
    return firstValueFrom(this.http.get<PlatformTheme>(`${this.base}/lojas/${id}/tema`));
  }

  previewTheme(id: string, draft: ThemeDraft): Promise<ThemePreview> {
    return firstValueFrom(this.http.post<ThemePreview>(`${this.base}/lojas/${id}/tema/previa`, draft, { context: silent() }));
  }

  saveTheme(id: string, draft: ThemeDraft): Promise<ThemeSaved> {
    return firstValueFrom(this.http.put<ThemeSaved>(`${this.base}/lojas/${id}/tema`, draft, { context: feedback('Tema salvo. A loja virtual mostra a nova versão em até 1 minuto.') }));
  }

  createPage(id: string, draft: PageDraft): Promise<ThemeSaved> {
    return firstValueFrom(this.http.post<ThemeSaved>(`${this.base}/lojas/${id}/paginas`, draft, { context: feedback('Página criada.') }));
  }

  updatePage(id: string, pageId: string, draft: PageDraft): Promise<ThemeSaved> {
    return firstValueFrom(this.http.put<ThemeSaved>(`${this.base}/lojas/${id}/paginas/${pageId}`, draft, { context: feedback('Página salva.') }));
  }

  deletePage(id: string, pageId: string): Promise<ThemeSaved> {
    return firstValueFrom(this.http.delete<ThemeSaved>(`${this.base}/lojas/${id}/paginas/${pageId}`, { context: feedback('Página removida.') }));
  }

  plans(): Promise<PlatformPlan[]> {
    return firstValueFrom(this.http.get<PlatformPlan[]>(`${this.base}/planos`));
  }

  setPlanPrice(code: string, valor: number): Promise<PlatformPlan[]> {
    return firstValueFrom(this.http.put<PlatformPlan[]>(`${this.base}/planos/${code}/preco`, { valor }, { context: feedback(null) }));
  }

  billing(id: string): Promise<PlatformBilling> {
    return firstValueFrom(this.http.get<PlatformBilling>(`${this.base}/lojas/${id}/cobranca`));
  }

  addSetupFee(id: string, valor: number, vencimento: string): Promise<PlatformBilling> {
    return firstValueFrom(this.http.post<PlatformBilling>(`${this.base}/lojas/${id}/cobranca/implantacao`, { valor, vencimento }, { context: feedback('Cobrança da implantação criada.') }));
  }

  recordPayment(id: string, invoiceId: string, observacao: string): Promise<PlatformBilling> {
    return firstValueFrom(this.http.post<PlatformBilling>(`${this.base}/lojas/${id}/cobranca/faturas/${invoiceId}/pagamento-manual`, { observacao }, { context: feedback('Pagamento registrado.') }));
  }

  cancelInvoice(id: string, invoiceId: string, observacao: string): Promise<PlatformBilling> {
    return firstValueFrom(this.http.post<PlatformBilling>(`${this.base}/lojas/${id}/cobranca/faturas/${invoiceId}/cancelar`, { observacao }, { context: feedback('Fatura cancelada.') }));
  }

  audit(filter: { loja?: string; acao?: string; email?: string; pagina?: number }): Promise<AuditPage> {
    let params = new HttpParams();
    for (const [key, value] of Object.entries(filter)) if (value) params = params.set(key, String(value));
    return firstValueFrom(this.http.get<AuditPage>(`${this.base}/auditoria`, { params }));
  }
}
