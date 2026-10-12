import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { IntegrationLog } from './integration-log';
import { IntegrationsPage } from './integrations';
import { IntegrationLogPage, MessageFailure } from './integrations.api';

describe('Integrações (RF28/RF29)', () => {
  const failure: MessageFailure = {
    id: 'f1',
    failedAt: '2026-10-12T12:00:00Z',
    description: 'Confirmar pagamento no gateway',
    attempts: 6,
    errorType: 'PaymentGatewayException',
    errorSummary: 'Mercado Pago respondeu 503.',
    status: 'pending',
    replayedAt: null,
    replayedByEmail: null,
    correlationId: 'c1',
  };
  const page: IntegrationLogPage = {
    items: [
      {
        id: 'l1', at: '2026-10-12T12:00:00Z', channel: 'mercado-pago', operation: 'consultar pagamento', success: false, durationMs: 120,
        errorSummary: 'Mercado Pago respondeu 503.', reference: null, orderId: 'o1', orderNumber: 42, partId: null, correlationId: 'c1',
      },
    ],
    total: 1,
    page: 1,
    pageSize: 50,
  };

  beforeEach(() => TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] }));

  async function settle(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }) {
    await fixture.whenStable();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  it('lista as falhas pendentes e o log; reprocessar recarrega as falhas', async () => {
    const fixture = TestBed.createComponent(IntegrationsPage);
    fixture.detectChanges();
    const backend = TestBed.inject(HttpTestingController);
    backend.expectOne((r) => r.url === '/api/painel/integracoes/falhas' && r.params.get('pendentes') === 'true').flush([failure]);
    backend.expectOne((r) => r.url === '/api/painel/integracoes/log').flush(page);
    await settle(fixture);
    const element = fixture.nativeElement as HTMLElement;

    expect(element.querySelector('[data-failures]')!.textContent).toContain('Confirmar pagamento no gateway');
    expect(element.querySelector('[data-log]')!.textContent).toContain('Mercado Pago');
    expect(element.querySelector('[data-log]')!.textContent).toContain('nº 42');

    element.querySelector<HTMLButtonElement>('[data-replay="f1"]')!.click();
    backend.expectOne({ method: 'POST', url: '/api/painel/integracoes/falhas/f1/reprocessar' }).flush(null, { status: 204, statusText: 'No Content' });
    await settle(fixture);
    backend.expectOne((r) => r.url === '/api/painel/integracoes/falhas').flush([]);
    await settle(fixture);

    expect(element.querySelector('[role=status]')!.textContent).toContain('Mandado reprocessar');
    expect(element.textContent).toContain('Nenhuma falha pendente');
    backend.verify();
  });

  it('filtrar por situação refaz a consulta', async () => {
    const fixture = TestBed.createComponent(IntegrationsPage);
    fixture.detectChanges();
    const backend = TestBed.inject(HttpTestingController);
    backend.expectOne((r) => r.url === '/api/painel/integracoes/falhas').flush([]);
    backend.expectOne((r) => r.url === '/api/painel/integracoes/log').flush(page);
    await settle(fixture);

    const status = (fixture.nativeElement as HTMLElement).querySelector<HTMLSelectElement>('select[aria-label="Situação"]')!;
    status.value = 'falha';
    status.dispatchEvent(new Event('change'));
    await settle(fixture);

    backend.expectOne((r) => r.url === '/api/painel/integracoes/log' && r.params.get('situacao') === 'falha').flush(page);
    backend.verify();
  });

  it('log do pedido busca pelas chamadas daquele pedido', async () => {
    const fixture = TestBed.createComponent(IntegrationLog);
    fixture.componentRef.setInput('orderId', 'o1');
    fixture.detectChanges();
    const backend = TestBed.inject(HttpTestingController);
    backend.expectOne((r) => r.url === '/api/painel/integracoes/log' && r.params.get('pedido') === 'o1').flush(page);
    await settle(fixture);

    expect((fixture.nativeElement as HTMLElement).querySelector('[data-integration-log]')!.textContent).toContain('Falhou');
    backend.verify();
  });
});
