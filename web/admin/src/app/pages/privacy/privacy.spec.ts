import { registerLocaleData } from '@angular/common';
import localePt from '@angular/common/locales/pt';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { LOCALE_ID } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { PrivacyPage } from './privacy';

registerLocaleData(localePt, 'pt-BR');

async function settle(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }) {
  await fixture.whenStable();
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

const emptyLog = { items: [], page: 1, pageSize: 50, hasMore: false };

describe('PrivacyPage', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), { provide: LOCALE_ID, useValue: 'pt-BR' }],
    }),
  );

  async function render() {
    const fixture = TestBed.createComponent(PrivacyPage);
    fixture.detectChanges();
    const backend = TestBed.inject(HttpTestingController);
    backend.expectOne((r) => r.url === '/api/painel/privacidade/acessos').flush({
      ...emptyLog,
      items: [{ at: '2026-10-15T12:00:00Z', userEmail: 'op@loja.test', action: 'pedido.aberto', orderId: 'o1', orderNumber: 12, details: null, ip: '10.0.0.1' }],
    });
    await settle(fixture);
    const element = fixture.nativeElement as HTMLElement;
    const type = (selector: string, value: string) => {
      const input = element.querySelector<HTMLInputElement>(selector)!;
      input.value = value;
      input.dispatchEvent(new Event('input'));
    };
    return { fixture, backend, element, type };
  }

  it('mostra o registro de acesso com a ação em português', async () => {
    const { element } = await render();
    expect(element.querySelector('[data-access-log] tbody')!.textContent).toContain('Abriu o pedido');
    expect(element.querySelector('[data-access-log] tbody')!.textContent).toContain('nº 12');
  });

  it('pedido em andamento bloqueia: mostra os números e não oferece apagar', async () => {
    const { fixture, backend, element, type } = await render();
    type('[data-email]', 'ana@exemplo.com.br');
    element.querySelector<HTMLButtonElement>('[data-search]')!.click();
    const preview = backend.expectOne('/api/painel/privacidade/titular/previa');
    expect(preview.request.body).toEqual({ email: 'ana@exemplo.com.br', documento: undefined });
    preview.flush({ orders: 2, inProgress: [31], withInvoice: 0, alreadyAnonymized: 0 });
    await settle(fixture);

    expect(element.querySelector('[data-in-progress]')!.textContent).toContain('nº 31');
    expect(element.querySelector('[data-anonymize-button]')).toBeNull();
  });

  it('apaga só depois de confirmar e recarrega o registro', async () => {
    const { fixture, backend, element, type } = await render();
    type('[data-document]', '123.456.789-09');
    element.querySelector<HTMLButtonElement>('[data-search]')!.click();
    backend.expectOne('/api/painel/privacidade/titular/previa').flush({ orders: 3, inProgress: [], withInvoice: 1, alreadyAnonymized: 0 });
    await settle(fixture);

    element.querySelector<HTMLButtonElement>('[data-anonymize-button]')!.click();
    await settle(fixture);
    backend.expectNone('/api/painel/privacidade/titular/anonimizar');
    element.querySelector<HTMLButtonElement>('[data-confirm]')!.click();
    const request = backend.expectOne('/api/painel/privacidade/titular/anonimizar');
    expect(request.request.body).toEqual({ email: undefined, documento: '123.456.789-09' });
    request.flush({ orders: 3, inProgress: [], withInvoice: 1, alreadyAnonymized: 0 });
    await settle(fixture);
    backend.expectOne((r) => r.url === '/api/painel/privacidade/acessos').flush(emptyLog);
    await settle(fixture);

    expect(element.querySelector('[data-done]')!.textContent).toContain('3 pedido(s); 1 mantêm nome e CPF/CNPJ');
  });
});
