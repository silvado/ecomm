import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { FiscalDocument, InvoiceDraft } from './fiscal.api';
import { FiscalSettingsSection } from './fiscal-settings';
import { OrderInvoice } from './order-invoice';

async function settle(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }) {
  for (let i = 0; i < 3; i++) {
    await fixture.whenStable();
    fixture.detectChanges();
  }
}

function type(element: HTMLElement, selector: string, value: string): void {
  const input = element.querySelector<HTMLInputElement>(selector)!;
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

const doc = (extra: Partial<FiscalDocument> = {}): FiscalDocument => ({
  id: 'd1', source: 'provider', status: 'authorized', environment: 'homologation', accessKey: '4226'.padEnd(44, '1'), number: 77, series: 1,
  message: null, requestedAt: '2026-10-12T12:00:00Z', authorizedAt: '2026-10-12T12:00:05Z', canceledAt: null, cancelJustification: null,
  hasXml: true, hasDanfe: true, canCancel: true, cancelDeadline: '2026-10-13T12:00:05Z', ...extra,
});

const draft = (extra: Partial<InvoiceDraft> = {}): InvoiceDraft => ({
  mode: 'provider',
  recipient: { name: 'Maria da Silva', document: '52998224725', email: 'maria@exemplo.com.br', address: null },
  problems: [],
  documents: [],
  ...extra,
});

describe('Nota fiscal', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] }));

  async function renderInvoice(first: InvoiceDraft) {
    const fixture = TestBed.createComponent(OrderInvoice);
    fixture.componentRef.setInput('orderId', 'o1');
    fixture.detectChanges();
    const backend = TestBed.inject(HttpTestingController);
    backend.expectOne('/api/painel/pedidos/o1/nfe').flush(first);
    await settle(fixture);
    return { fixture, backend, element: fixture.nativeElement as HTMLElement };
  }

  it('retirada pede o endereço do destinatário e emite', async () => {
    const { fixture, backend, element } = await renderInvoice(draft());
    element.querySelector<HTMLButtonElement>('[data-start-emit]')!.click();
    await settle(fixture);
    expect(element.querySelector<HTMLInputElement>('[data-recipient-document]')!.value).toBe('52998224725');

    type(element, '[data-recipient-cep]', '88015-200');
    await settle(fixture);
    element.querySelector<HTMLButtonElement>('[data-emit]')!.click();
    const emit = backend.expectOne({ method: 'POST', url: '/api/painel/pedidos/o1/nfe' });
    expect(emit.request.body.recipient.name).toBe('Maria da Silva');
    expect(emit.request.body.recipient.address.postalCode).toBe('88015-200');
    expect(emit.request.body.recipient.address.complement).toBeNull();
    emit.flush(draft({ documents: [doc({ status: 'processing', accessKey: null, number: null })] }));
    await settle(fixture);
    backend.expectOne('/api/painel/pedidos/o1/nfe').flush(draft({ documents: [doc()] }));
    await settle(fixture);
    expect(element.querySelector('[data-invoice="authorized"]')!.textContent).toContain('NF-e nº 77');
    expect(element.querySelector('[data-key]')!.textContent).toHaveLength(44);
  });

  it('rejeitada mostra o motivo e permite emitir de novo; problemas bloqueiam', async () => {
    const { element } = await renderInvoice(draft({ documents: [doc({ status: 'rejected', message: 'Rejeição 778: NCM inexistente' })] }));
    expect(element.querySelector('[data-rejection]')!.textContent).toContain('NCM inexistente');
    expect(element.querySelector('[data-start-emit]')!.textContent).toContain('Emitir de novo');

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const blocked = await renderInvoice(draft({ problems: ['Peça K-1 sem NCM (e a loja não tem NCM padrão).'] }));
    expect(blocked.element.querySelector('[data-problems]')!.textContent).toContain('sem NCM');
    expect(blocked.element.querySelector('[data-start-emit]')).toBeNull();
  });

  it('cancela com justificativa', async () => {
    const { fixture, backend, element } = await renderInvoice(draft({ documents: [doc()] }));
    element.querySelector<HTMLButtonElement>('[data-cancel-invoice]')!.click();
    await settle(fixture);
    const area = element.querySelector<HTMLTextAreaElement>('[data-justification]')!;
    area.value = 'Venda desfeita a pedido do cliente';
    area.dispatchEvent(new Event('input'));
    await settle(fixture);
    element.querySelector<HTMLButtonElement>('[data-confirm-cancel]')!.click();
    const cancel = backend.expectOne({ method: 'POST', url: '/api/painel/pedidos/o1/nfe/d1/cancelar' });
    expect(cancel.request.body).toEqual({ justification: 'Venda desfeita a pedido do cliente' });
    cancel.flush(draft({ documents: [doc({ status: 'canceled', canCancel: false, cancelJustification: 'Venda desfeita a pedido do cliente' })] }));
    await settle(fixture);
    expect(element.querySelector('[data-invoice="canceled"]')).not.toBeNull();
    backend.verify();
  });

  it('modo externo registra pela chave', async () => {
    const { fixture, backend, element } = await renderInvoice(draft({ mode: 'external' }));
    type(element, '[data-access-key]', ' 4226 1111 ');
    await settle(fixture);
    element.querySelector<HTMLButtonElement>('[data-register-external]')!.click();
    const register = backend.expectOne({ method: 'POST', url: '/api/painel/pedidos/o1/nfe/externa' });
    expect(register.request.body).toEqual({ accessKey: '4226 1111', xmlBase64: null });
    register.flush({ title: 'Chave de acesso inválida: confira os números.' }, { status: 400, statusText: 'Bad Request' });
    await settle(fixture);
    expect(element.querySelector('[role=alert]')!.textContent).toContain('Chave de acesso inválida');
  });

  it('configuração da loja envia campos vazios como nulos', async () => {
    const fixture = TestBed.createComponent(FiscalSettingsSection);
    fixture.detectChanges();
    const backend = TestBed.inject(HttpTestingController);
    backend.expectOne('/api/painel/loja/fiscal').flush({
      settings: { mode: 'disabled', environment: 'homologation', regime: 'simplesNacional', state: null, defaultNcm: null, cfopInState: '5102',
        cfopOutOfState: '6102', defaultTaxCode: '102', defaultOrigin: 0 },
      issuerCnpj: '11222333000181',
      providerConnected: false,
    });
    await settle(fixture);
    const element = fixture.nativeElement as HTMLElement;
    const mode = element.querySelector<HTMLSelectElement>('[data-mode]')!;
    mode.value = 'external';
    mode.dispatchEvent(new Event('change'));
    await settle(fixture);
    type(element, '[data-ncm]', '  ');
    await settle(fixture);
    element.querySelector<HTMLButtonElement>('[data-save-fiscal]')!.click();
    const save = backend.expectOne({ method: 'PUT', url: '/api/painel/loja/fiscal' });
    expect(save.request.body.mode).toBe('external');
    expect(save.request.body.defaultNcm).toBeNull();
    expect(element.textContent).toContain('11222333000181');
  });
});
