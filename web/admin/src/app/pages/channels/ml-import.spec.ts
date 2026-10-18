import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ChannelImport, MercadoLivreImportSection } from './ml-import';

async function settle(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }) {
  for (let i = 0; i < 3; i++) {
    await fixture.whenStable();
    fixture.detectChanges();
  }
}

const base = '/api/painel/canais/mercado-livre/importacoes';

function importOf(over: Partial<ChannelImport>): ChannelImport {
  return {
    id: 'i1', status: 'processing', createdAt: '2026-10-18T12:00:00Z', finishedAt: null, totalItems: 12, truncated: false, processedItems: 4,
    createdCount: 3, linkedCount: 1, alreadyLinkedCount: 0, rejectedCount: 0, issues: [], failureReason: null, ...over,
  };
}

describe('MercadoLivreImportSection (RF11)', () => {
  beforeEach(() => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
  });
  afterEach(() => vi.useRealTimers());

  it('começa a importação, acompanha o progresso e mostra o relatório', async () => {
    const fixture = TestBed.createComponent(MercadoLivreImportSection);
    fixture.detectChanges();
    const backend = TestBed.inject(HttpTestingController);
    backend.expectOne(base).flush([]);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-start-import]')!.textContent).toContain('Importar anúncios');

    el.querySelector<HTMLButtonElement>('[data-start-import]')!.click();
    backend.expectOne({ method: 'POST', url: base }).flush(importOf({}));
    await settle(fixture);
    expect(el.querySelector('[data-progress]')!.textContent).toContain('Importando: 4 de 12.');
    expect(el.querySelector<HTMLButtonElement>('[data-start-import]')!.disabled).toBe(true);

    await vi.advanceTimersByTimeAsync(2100);
    backend.expectOne(`${base}/i1`).flush(importOf({
      status: 'completed', processedItems: 12, createdCount: 9, linkedCount: 1, rejectedCount: 2,
      issues: [{ externalId: 'MLB9', message: 'Anúncio com variações: cadastre cada variação como uma peça.' }],
    }));
    await settle(fixture);
    expect(el.querySelector('[data-result]')!.textContent).toContain('9 peças novas, 1 ligadas a peças existentes');
    expect(el.querySelector('[data-issues]')!.textContent).toContain('MLB9');
    expect(el.querySelector('[data-start-import]')!.textContent).toContain('Importar de novo');
    backend.verify();
  });

  it('mostra o motivo quando a importação parou', async () => {
    const fixture = TestBed.createComponent(MercadoLivreImportSection);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne(base).flush([importOf({ status: 'failed', failureReason: 'O Mercado Livre pediu nova autorização.' })]);
    await settle(fixture);
    expect((fixture.nativeElement as HTMLElement).querySelector('[role=alert]')!.textContent).toContain('pediu nova autorização');
  });
});
