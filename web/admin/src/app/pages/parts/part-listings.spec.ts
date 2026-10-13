import { registerLocaleData } from '@angular/common';
import localePt from '@angular/common/locales/pt';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ListingView, PartListings } from './part-listings';

registerLocaleData(localePt, 'pt-BR');

async function settle(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }) {
  for (let i = 0; i < 3; i++) {
    await fixture.whenStable();
    fixture.detectChanges();
  }
}

const listing = (extra: Partial<ListingView> = {}): ListingView => ({
  id: 'l1', channel: 'canal-simulado', externalAccount: 'vend-1', externalId: 'SIM-1', status: 'active', lastPushedQuantity: 2,
  lastPushedPrice: 100, lastSyncedAt: '2026-10-13T12:00:00Z', lastError: null, ...extra,
});

describe('Anúncios da peça', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] }));

  async function render(items: ListingView[]) {
    const fixture = TestBed.createComponent(PartListings);
    fixture.componentRef.setInput('partId', 'p1');
    fixture.detectChanges();
    const backend = TestBed.inject(HttpTestingController);
    backend.expectOne('/api/painel/pecas/p1/anuncios').flush(items);
    await settle(fixture);
    return { fixture, backend, element: fixture.nativeElement as HTMLElement };
  }

  it('mostra a situação e pausa pelo lojista', async () => {
    const { fixture, backend, element } = await render([listing()]);
    expect(element.querySelector('[data-listing="active"]')!.textContent).toContain('Canal simulado (teste)');
    element.querySelector<HTMLButtonElement>('[data-pause]')!.click();
    backend.expectOne({ method: 'POST', url: '/api/painel/anuncios/l1/pausar' }).flush(null, { status: 204, statusText: 'No Content' });
    await settle(fixture);
    backend.expectOne('/api/painel/pecas/p1/anuncios').flush([listing({ status: 'pausedByUser' })]);
    await settle(fixture);
    expect(element.querySelector('[data-listing="pausedByUser"]')!.textContent).toContain('Pausado por você');
    expect(element.querySelector('[data-resume]')).not.toBeNull();
  });

  it('pausado pelo sistema mostra o motivo e erro aparece', async () => {
    const { element } = await render([listing({ status: 'pausedBySystem' }), listing({ id: 'l2', status: 'error', lastError: 'Canal fora do ar' })]);
    expect(element.textContent).toContain('Pausado (sem estoque)');
    expect(element.textContent).toContain('Canal fora do ar');
  });

  it('mostra quando foi conferido no canal', async () => {
    const { element } = await render([listing({ lastReconciledAt: '2026-10-13T15:00:00Z' })]);
    expect(element.querySelector('[data-reconciled]')!.textContent).toContain('13/10/2026');
  });

  it('sem anúncios avisa', async () => {
    const { element } = await render([]);
    expect(element.textContent).toContain('ainda não está anunciada');
  });
});
