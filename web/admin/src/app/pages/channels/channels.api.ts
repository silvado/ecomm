import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { feedback } from '../../core/feedback';

export type ChannelConnectionStatus = 'active' | 'reauthRequired' | 'disabled';

/** Conta da loja num canal de venda (RF21). Nunca traz tokens. */
export interface ChannelConnection {
  id: string;
  channel: string;
  externalAccountId: string;
  accountName: string | null;
  status: ChannelConnectionStatus;
  statusReason: string | null;
  createdAt: string;
  updatedAt: string | null;
}

export interface ChannelsOverview {
  /** A instalação tem o app do Mercado Livre configurado. */
  mercadoLivreAvailable: boolean;
  /** O plano da loja inclui o Mercado Livre. */
  mercadoLivreInPlan: boolean;
  connections: ChannelConnection[];
}

/** Catálogo do Instagram/Facebook ligado (RF22): o endereço do feed vai no Commerce Manager. */
export interface MetaCatalogView {
  connectionId: string;
  catalogId: string;
  catalogName: string | null;
  feedUrl: string;
}

export const CONNECTION_STATUS_LABELS: Record<ChannelConnectionStatus, string> = {
  active: 'Conectada',
  reauthRequired: 'Autorizar de novo',
  disabled: 'Desconectada',
};

/** Canais de venda da loja (RF21): conectar o Mercado Livre por OAuth, ver a situação e desconectar. */
@Injectable({ providedIn: 'root' })
export class ChannelsApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/painel/canais';

  overview(): Promise<ChannelsOverview> {
    return firstValueFrom(this.http.get<ChannelsOverview>(this.base));
  }

  /** Endereço da página de autorização do Mercado Livre (o navegador vai para lá). */
  async startMercadoLivre(): Promise<string> {
    return (await firstValueFrom(this.http.post<{ authorizationUrl: string }>(`${this.base}/mercado-livre/conectar`, {}, { context: feedback(null) }))).authorizationUrl;
  }

  /** Repassa a volta do Mercado Livre (código e state, ou o erro se a loja negou). */
  completeMercadoLivre(code: string | null, state: string | null, error: string | null): Promise<ChannelConnection> {
    return firstValueFrom(this.http.post<ChannelConnection>(`${this.base}/mercado-livre/retorno`, { code, state, error }, { context: feedback(null) }));
  }

  disconnect(id: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`${this.base}/${id}`, { context: feedback('Conta desconectada.') }));
  }

  /** Nulo quando não há catálogo ligado (204). */
  async meta(): Promise<MetaCatalogView | null> {
    return (await firstValueFrom(this.http.get<MetaCatalogView | null>(`${this.base}/meta`))) ?? null;
  }

  connectMeta(catalogId: string, accessToken: string): Promise<MetaCatalogView> {
    return firstValueFrom(this.http.post<MetaCatalogView>(`${this.base}/meta/conectar`, { catalogId, accessToken }, { context: feedback('Catálogo conectado.') }));
  }

  disconnectMeta(): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`${this.base}/meta`, { context: feedback('Catálogo desconectado.') }));
  }
}
