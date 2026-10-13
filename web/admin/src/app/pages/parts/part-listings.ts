import { CurrencyPipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { Component, inject, Injectable, input, OnInit, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { problemTitle } from '../../core/auth';

export type ListingStatus = 'pending' | 'active' | 'pausedBySystem' | 'pausedByUser' | 'closed' | 'error';

export interface ListingView {
  id: string;
  channel: string;
  externalAccount: string;
  externalId: string | null;
  status: ListingStatus;
  lastPushedQuantity: number | null;
  lastPushedPrice: number | null;
  lastSyncedAt: string | null;
  lastError: string | null;
  lastReconciledAt?: string | null;
}

export const LISTING_STATUS: Record<ListingStatus, string> = {
  pending: 'Aguardando publicação',
  active: 'Ativo',
  pausedBySystem: 'Pausado (sem estoque)',
  pausedByUser: 'Pausado por você',
  closed: 'Encerrado',
  error: 'Erro na última atualização',
};

export const CHANNEL_NAMES: Record<string, string> = {
  'canal-simulado': 'Canal simulado (teste)',
  'mercado-livre': 'Mercado Livre',
  meta: 'Instagram / Facebook',
  olx: 'OLX',
};

/** Anúncios da peça nos canais (E2): situação e pausa do lojista. */
@Injectable({ providedIn: 'root' })
export class ListingsApi {
  private readonly http = inject(HttpClient);

  forPart(partId: string): Promise<ListingView[]> {
    return firstValueFrom(this.http.get<ListingView[]>(`/api/painel/pecas/${partId}/anuncios`));
  }

  change(listingId: string, action: 'pausar' | 'retomar'): Promise<unknown> {
    return firstValueFrom(this.http.post(`/api/painel/anuncios/${listingId}/${action}`, {}));
  }
}

const WHEN = new Intl.DateTimeFormat('pt-BR', { timeZone: 'America/Sao_Paulo', dateStyle: 'short', timeStyle: 'short' });

/**
 * Onde a peça está anunciada. O estoque manda: zerou, o anúncio pausa sozinho; voltou, reativa. Pausado pelo lojista
 * fica pausado até ele retomar (RF24).
 */
@Component({
  selector: 'app-part-listings',
  imports: [CurrencyPipe],
  template: `
    <section data-part-listings>
      <h2>Anúncios</h2>
      @if (listings().length) {
        <table>
          <thead><tr><th>Canal</th><th>Situação</th><th>Quantidade e preço no canal</th><th>Atualizado</th><th>Conferido</th><th></th></tr></thead>
          <tbody>
            @for (l of listings(); track l.id) {
              <tr [attr.data-listing]="l.status">
                <td>{{ channelName(l.channel) }}<br /><small class="muted">{{ l.externalId }}</small></td>
                <td>
                  {{ statusLabel[l.status] }}
                  @if (l.lastError) {
                    <br /><small class="error">{{ l.lastError }}</small>
                  }
                </td>
                <td>{{ l.lastPushedQuantity ?? '—' }} un. · {{ l.lastPushedPrice != null ? (l.lastPushedPrice | currency: 'BRL' : 'symbol' : '1.2-2' : 'pt-BR') : '—' }}</td>
                <td>{{ l.lastSyncedAt ? when(l.lastSyncedAt) : '—' }}</td>
                <td data-reconciled>{{ l.lastReconciledAt ? when(l.lastReconciledAt) : '—' }}</td>
                <td>
                  @if (l.status === 'active' || l.status === 'pausedBySystem' || l.status === 'error') {
                    <button type="button" class="link" (click)="change(l, 'pausar')" [disabled]="busy()" data-pause>Pausar</button>
                  } @else if (l.status === 'pausedByUser') {
                    <button type="button" class="link" (click)="change(l, 'retomar')" [disabled]="busy()" data-resume>Retomar</button>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
        <small class="muted">Sem estoque, o anúncio pausa sozinho e volta quando houver saldo. Pausado por você só volta quando você retomar. De hora em hora conferimos o anúncio no canal e corrigimos o que estiver diferente do estoque.</small>
      } @else {
        <p class="muted">A peça ainda não está anunciada em nenhum canal. A publicação no Mercado Livre e no Instagram entra em breve.</p>
      }
      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
    </section>
  `,
})
export class PartListings implements OnInit {
  readonly partId = input.required<string>();

  private readonly api = inject(ListingsApi);

  protected readonly listings = signal<ListingView[]>([]);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly statusLabel = LISTING_STATUS;
  protected readonly when = (iso: string) => WHEN.format(new Date(iso));
  protected readonly channelName = (c: string) => CHANNEL_NAMES[c] ?? c;

  async ngOnInit(): Promise<void> {
    await this.load();
  }

  protected async change(listing: ListingView, action: 'pausar' | 'retomar'): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await this.api.change(listing.id, action);
      await this.load();
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }

  private async load(): Promise<void> {
    try {
      this.listings.set(await this.api.forPart(this.partId()));
    } catch (error) {
      this.error.set(problemTitle(error));
    }
  }
}
