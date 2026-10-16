import { registerLocaleData } from '@angular/common';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import localePt from '@angular/common/locales/pt';
import { TestBed } from '@angular/core/testing';
import { PlatformBilling } from './platform.api';
import { PlatformBillingSection } from './tenant-billing';

registerLocaleData(localePt, 'pt-BR');

const billing: PlatformBilling = {
  tenantId: 't1',
  planName: 'Essencial',
  monthlyPrice: 149.9,
  startedOn: null,
  dueDay: null,
  invoices: [],
};

const open = { id: 'f1', kind: 'monthly' as const, description: 'Mensalidade 10/2026 — plano Essencial', amount: 149.9, dueDate: '2026-10-16', status: 'open' as const, overdue: true, paidAt: null, paidVia: null, note: null };

async function settle(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }) {
  await fixture.whenStable();
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

function type(element: HTMLElement, selector: string, value: string) {
  const input = element.querySelector<HTMLInputElement>(selector)!;
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

describe('Cobrança da loja na plataforma (RF38)', () => {
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

  async function render(b: PlatformBilling) {
    const fixture = TestBed.createComponent(PlatformBillingSection);
    fixture.componentRef.setInput('tenantId', 't1');
    fixture.detectChanges();
    backend.expectOne('/api/plataforma/lojas/t1/cobranca').flush(b);
    await settle(fixture);
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  it('loja em implantação: avisa que a mensalidade começa na publicação e permite lançar a implantação', async () => {
    const { fixture, element } = await render(billing);

    expect(element.querySelector('[data-billing-summary]')!.textContent).toContain('quando o Dono publicar');
    type(element, '[data-setup-amount]', '1500');
    type(element, '[data-setup-due]', '2026-10-30');
    fixture.detectChanges();
    element.querySelector<HTMLButtonElement>('[data-add-setup]')!.click();
    const request = backend.expectOne('/api/plataforma/lojas/t1/cobranca/implantacao');
    expect(request.request.body).toEqual({ valor: 1500, vencimento: '2026-10-30' });
    request.flush({ ...billing, invoices: [{ ...open, id: 's1', kind: 'setup', description: 'Taxa de implantação', amount: 1500 }] });
    await settle(fixture);

    expect(element.querySelector('[data-add-setup]')).toBeNull();
  });

  it('registrar pagamento pede como foi pago', async () => {
    const { fixture, element } = await render({ ...billing, startedOn: '2026-10-16', dueDay: 16, invoices: [open] });

    element.querySelector<HTMLButtonElement>('[data-record-payment]')!.click();
    await settle(fixture);
    expect(element.querySelector<HTMLButtonElement>('[data-confirm-billing]')!.disabled).toBe(true);
    type(element, '[data-note]', 'Pix em 16/10');
    fixture.detectChanges();
    element.querySelector<HTMLButtonElement>('[data-confirm-billing]')!.click();
    const request = backend.expectOne('/api/plataforma/lojas/t1/cobranca/faturas/f1/pagamento-manual');
    expect(request.request.body).toEqual({ observacao: 'Pix em 16/10' });
    request.flush({ ...billing, invoices: [{ ...open, status: 'paid', overdue: false, paidVia: 'manual', note: 'Pix em 16/10' }] });
    await settle(fixture);

    expect(element.querySelector('[data-invoice="f1"]')!.textContent).toContain('Paga (registrada pela equipe)');
    expect(element.querySelector('[data-record-payment]')).toBeNull();
  });
});
