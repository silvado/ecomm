import { registerLocaleData } from '@angular/common';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import localePt from '@angular/common/locales/pt';
import { TestBed } from '@angular/core/testing';
import { SubscriptionPage, SubscriptionView } from './subscription';

registerLocaleData(localePt, 'pt-BR');

const view: SubscriptionView = {
  planCode: 'essencial',
  planName: 'Essencial',
  monthlyPrice: 149.9,
  startedOn: '2026-10-16',
  dueDay: 16,
  onlinePayment: true,
  invoices: [
    { id: 'f2', kind: 'monthly', description: 'Mensalidade 11/2026 — plano Essencial', amount: 149.9, dueDate: '2026-11-16', status: 'open', overdue: false, paidAt: null, paidVia: null, note: null },
    { id: 'f1', kind: 'monthly', description: 'Mensalidade 10/2026 — plano Essencial', amount: 149.9, dueDate: '2026-10-16', status: 'open', overdue: true, paidAt: null, paidVia: null, note: null },
    { id: 'f0', kind: 'setup', description: 'Taxa de implantação', amount: 1500, dueDate: '2026-10-10', status: 'paid', overdue: false, paidAt: '2026-10-11T12:00:00Z', paidVia: 'manual', note: 'Pix' },
  ],
};

async function settle(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }) {
  await fixture.whenStable();
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

describe('Assinatura (RF38)', () => {
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

  async function open(v: SubscriptionView) {
    const fixture = TestBed.createComponent(SubscriptionPage);
    fixture.detectChanges();
    backend.expectOne('/api/painel/assinatura').flush(v);
    await settle(fixture);
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  it('mostra plano, vencimento e a situação de cada fatura', async () => {
    const { element } = await open(view);

    expect(element.querySelector('[data-plan]')!.textContent).toContain('Essencial');
    expect(element.textContent).toContain('vence todo dia 16');
    expect(element.querySelector('[data-invoice="f1"]')!.textContent).toContain('Vencida');
    expect(element.querySelector('[data-invoice="f2"]')!.textContent).toContain('Em aberto');
    expect(element.querySelector('[data-invoice="f0"]')!.textContent).toContain('Paga (registrada pela equipe)');
    expect(element.querySelectorAll('[data-pay]').length).toBe(2);
  });

  it('sem gateway, não oferece Pagar e orienta falar com a equipe', async () => {
    const { element } = await open({ ...view, onlinePayment: false });

    expect(element.querySelector('[data-offline]')).not.toBeNull();
    expect(element.querySelector('[data-pay]')).toBeNull();
  });

  it('mostra o motivo quando a fatura não pode ser paga', async () => {
    const { fixture, element } = await open(view);

    element.querySelector<HTMLButtonElement>('[data-invoice="f1"] [data-pay]')!.click();
    backend
      .expectOne((r) => r.method === 'POST' && r.url === '/api/painel/assinatura/faturas/f1/pagar')
      .flush({ title: 'Esta fatura já foi paga ou cancelada.' }, { status: 409, statusText: 'Conflict' });
    await settle(fixture);

    expect(element.querySelector('[role="alert"]')!.textContent).toContain('já foi paga');
  });
});
