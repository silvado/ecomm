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

  it('anuncia no Mercado Livre: categoria sugerida, formulário pré-preenchido e publicação (RF21)', async () => {
    const { fixture, backend, element } = await render([]);
    element.querySelector<HTMLButtonElement>('[data-ml-start]')!.click();

    backend.expectOne('/api/painel/pecas/p1/mercado-livre/categorias').flush([
      { categoryId: 'MLB-FAROL', categoryName: 'Faróis', domainName: 'Faróis' },
      { categoryId: 'MLB-OUTROS', categoryName: 'Outras peças', domainName: null },
    ]);
    await settle(fixture);
    const form = backend.expectOne((r) => r.url === '/api/painel/pecas/p1/mercado-livre/formulario');
    expect(form.request.params.get('categoria')).toBe('MLB-FAROL');
    form.flush({
      category: { categoryId: 'MLB-FAROL', name: 'Faróis', path: 'Peças > Faróis', maxTitleLength: 60, maxPictures: 12, listingAllowed: true },
      attributes: [
        { id: 'BRAND', name: 'Marca', valueType: 'string', required: true, conditionalRequired: false, multivalued: false, maxLength: 255, values: [], units: [], defaultUnit: null },
        { id: 'ITEM_CONDITION', name: 'Condição do item', valueType: 'list', required: true, conditionalRequired: false, multivalued: false, maxLength: null,
          values: [{ id: '2230284', name: 'Novo' }, { id: '2230581', name: 'Usado' }], units: [], defaultUnit: null },
        { id: 'SIDE', name: 'Lado', valueType: 'list', required: false, conditionalRequired: false, multivalued: false, maxLength: null,
          values: [{ id: 'S1', name: 'Esquerdo' }], units: [], defaultUnit: null },
      ],
      prefilled: [{ id: 'ITEM_CONDITION', valueId: '2230581', valueName: null }],
      warnings: ['A peça está sem as medidas da embalagem.'],
    });
    await settle(fixture);

    expect(element.querySelector('[data-ml-attribute="BRAND"]')!.textContent).toContain('Marca *');
    expect(element.querySelector('details [data-ml-attribute="SIDE"]')).not.toBeNull();
    expect(element.textContent).toContain('sem as medidas da embalagem');
    const brand = element.querySelector<HTMLInputElement>('[data-ml-attribute="BRAND"] input')!;
    brand.value = 'VW';
    brand.dispatchEvent(new Event('input'));
    await settle(fixture);
    element.querySelector<HTMLButtonElement>('[data-ml-publish-button]')!.click();

    const publish = backend.expectOne({ method: 'POST', url: '/api/painel/pecas/p1/mercado-livre/anunciar' });
    expect(publish.request.body).toEqual({
      categoryId: 'MLB-FAROL', listingTypeId: 'gold_special', warrantyType: null, warrantyTime: null,
      attributes: [{ id: 'ITEM_CONDITION', valueId: '2230581', valueName: null }, { id: 'BRAND', valueId: null, valueName: 'VW' }],
    });
    publish.flush({
      listings: [listing({ channel: 'mercado-livre', externalId: 'MLB123' })],
      compatibilities: { sent: 12, unmapped: ['Ford Ka 2015'], error: null },
    });
    await settle(fixture);
    expect(element.textContent).toContain('Mercado Livre');
    expect(element.querySelector('[data-compat-report]')!.textContent).toContain('12 veículo(s)');
    expect(element.querySelector('[data-compat-report]')!.textContent).toContain('Ford Ka 2015');

    element.querySelector<HTMLButtonElement>('[data-ml-compat]')!.click();
    backend.expectOne({ method: 'POST', url: '/api/painel/anuncios/l1/compatibilidades' }).flush({ sent: 3, unmapped: [], error: null });
    await settle(fixture);
    expect(element.querySelector('[data-compat-report]')!.textContent).toContain('3 veículo(s)');
    expect(element.querySelector('[data-ml-start]')).toBeNull();
    expect(element.querySelector('[data-ml-publish]')).toBeNull();
  });

  it('sem conta conectada mostra o motivo', async () => {
    const { fixture, backend, element } = await render([]);
    element.querySelector<HTMLButtonElement>('[data-ml-start]')!.click();
    backend
      .expectOne('/api/painel/pecas/p1/mercado-livre/categorias')
      .flush({ title: 'Conecte a conta do Mercado Livre em Canais de venda antes de anunciar.' }, { status: 409, statusText: 'Conflict' });
    await settle(fixture);
    expect(element.querySelector('[data-ml-publish] [role=alert]')!.textContent).toContain('Conecte a conta do Mercado Livre');
  });
});
