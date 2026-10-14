import { registerLocaleData } from '@angular/common';
import localePt from '@angular/common/locales/pt';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { LOCALE_ID } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { CustomDomain, CustomDomains, DomainSettingsSection } from './domain-settings';

registerLocaleData(localePt, 'pt-BR');

const pending: CustomDomain = {
  id: 'd1',
  host: 'www.loja.test',
  kind: 'customWww',
  status: 'pending',
  isPrimary: false,
  requestedAt: '2026-10-13T12:00:00Z',
  lastCheckedAt: null,
  lastCheckMessage: null,
  verifiedAt: null,
  records: [
    { type: 'CNAME', name: 'www.loja.test', value: 'demo.plataforma.test', note: 'Aponta o endereço para a loja.', alternative: false },
    { type: 'TXT', name: '_ecomm-verificacao.www.loja.test', value: 'ecomm-verificacao=abc', note: 'Prova que o domínio é seu.', alternative: false },
  ],
};

const empty: CustomDomains = { allowed: true, platformHost: 'demo.plataforma.test', domains: [] };

async function settle(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }) {
  await fixture.whenStable();
  fixture.detectChanges();
  await fixture.whenStable();
}

describe('DomainSettingsSection', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), { provide: LOCALE_ID, useValue: 'pt-BR' }] }),
  );

  async function render(view: CustomDomains) {
    const fixture = TestBed.createComponent(DomainSettingsSection);
    fixture.detectChanges();
    const backend = TestBed.inject(HttpTestingController);
    backend.expectOne('/api/painel/loja/dominios').flush(view);
    await settle(fixture);
    return { fixture, backend, element: fixture.nativeElement as HTMLElement };
  }

  it('plano sem o recurso mostra o aviso e não deixa adicionar', async () => {
    const { element } = await render({ ...empty, allowed: false });
    expect(element.querySelector('[data-domain-plan]')).toBeTruthy();
    expect(element.querySelector('[data-domain-input]')).toBeNull();
  });

  it('adiciona o domínio e mostra os registros a criar no DNS', async () => {
    const { fixture, backend, element } = await render(empty);
    const input = element.querySelector<HTMLInputElement>('[data-domain-input]')!;
    input.value = ' www.loja.test ';
    input.dispatchEvent(new Event('input'));
    await settle(fixture);
    element.querySelector<HTMLButtonElement>('[data-domain-add]')!.click();

    const add = backend.expectOne({ method: 'POST', url: '/api/painel/loja/dominios' });
    expect(add.request.body).toEqual({ dominio: 'www.loja.test' });
    add.flush({ ...empty, domains: [pending] });
    await settle(fixture);

    const card = element.querySelector('[data-domain="www.loja.test"]')!;
    expect(card.textContent).toContain('Aguardando o DNS');
    expect(card.textContent).toContain('_ecomm-verificacao.www.loja.test');
    expect(card.textContent).toContain('ecomm-verificacao=abc');
    expect(element.querySelector<HTMLInputElement>('[data-domain-input]')!.value).toBe('');
  });

  it('verificar agora mostra o motivo e, quando acerta, vira endereço principal', async () => {
    const { fixture, backend, element } = await render({ ...empty, domains: [pending] });
    element.querySelector<HTMLButtonElement>('[data-domain-verify]')!.click();
    backend.expectOne({ method: 'POST', url: '/api/painel/loja/dominios/d1/verificar' })
      .flush({ ...empty, domains: [{ ...pending, lastCheckMessage: 'Não encontramos o registro TXT.' }] });
    await settle(fixture);
    expect(element.querySelector('[data-domain-message]')!.textContent).toContain('registro TXT');

    element.querySelector<HTMLButtonElement>('[data-domain-verify]')!.click();
    backend.expectOne({ method: 'POST', url: '/api/painel/loja/dominios/d1/verificar' })
      .flush({ ...empty, domains: [{ ...pending, status: 'verified', isPrimary: true, verifiedAt: '2026-10-13T13:00:00Z' }] });
    await settle(fixture);
    expect(element.textContent).toContain('Funcionando');
    expect(element.textContent).toContain('Endereço principal');
    expect(element.querySelector('[data-domain-verify]')).toBeNull();
    backend.verify();
  });

  it('remover pede confirmação', async () => {
    const { fixture, backend, element } = await render({ ...empty, domains: [pending] });
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true);
    element.querySelector<HTMLButtonElement>('[data-domain-remove]')!.click();
    expect(confirm).toHaveBeenCalled();
    backend.expectOne({ method: 'DELETE', url: '/api/painel/loja/dominios/d1' }).flush(empty);
    await settle(fixture);
    expect(element.querySelector('[data-domain]')).toBeNull();
    confirm.mockRestore();
  });
});
