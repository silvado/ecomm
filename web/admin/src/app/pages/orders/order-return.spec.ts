import { registerLocaleData } from '@angular/common';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import localePt from '@angular/common/locales/pt';
import { TestBed } from '@angular/core/testing';
import { OrderView } from './orders.api';
import { OrderReturnForm } from './order-return';

registerLocaleData(localePt, 'pt-BR');

describe('OrderReturnForm', () => {
  const order = {
    id: 'o1',
    number: 42,
    status: 'pickedUp',
    deliveredAt: new Date(Date.now() - 3 * 24 * 60 * 60 * 1000).toISOString(),
    items: [
      { partId: 'p1', internalCode: 'K-1', title: 'Farol', unitPrice: 100, quantity: 2, returnedQuantity: 1 },
      { partId: 'p2', internalCode: 'K-2', title: 'Lanterna', unitPrice: 50, quantity: 1, returnedQuantity: 0 },
    ],
  } as unknown as OrderView;

  async function setup() {
    TestBed.configureTestingModule({ imports: [OrderReturnForm], providers: [provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(OrderReturnForm);
    fixture.componentRef.setInput('order', order);
    const done: OrderView[] = [];
    fixture.componentInstance.done.subscribe((o) => done.push(o));
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const settle = async () => {
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
    };
    const set = async (selector: string, value: string, event = 'input') => {
      const el = element.querySelector<HTMLInputElement | HTMLSelectElement>(selector)!;
      el.value = value;
      el.dispatchEvent(new Event(event));
      await settle();
    };
    return { element, done, settle, set, backend: TestBed.inject(HttpTestingController) };
  }

  it('limita ao que ainda pode voltar, marca avaria e envia motivo e reembolso', async () => {
    const { element, done, settle, set, backend } = await setup();
    const quantities = Array.from(element.querySelectorAll<HTMLInputElement>('tbody input[type=number]'));

    expect(element.textContent).toContain('há 3 dias');
    expect(quantities.map((q) => q.max)).toEqual(['1', '1']);
    const submit = element.querySelector<HTMLButtonElement>('button[type=submit]')!;
    expect(submit.disabled).toBe(true);

    await set('tbody tr:nth-child(2) input[type=number]', '1');
    element.querySelector<HTMLInputElement>('tbody tr:nth-child(2) input[type=checkbox]')!.click();
    await settle();
    expect(element.querySelector('tbody tr:nth-child(2)')!.textContent).toContain('Não (avariada)');
    await set('select[name=reason]', 'defect', 'change');
    await set('input[name=refund]', '50');
    await set('select[name=method]', 'pix', 'change');
    expect(submit.disabled).toBe(false);
    submit.click();

    const request = backend.expectOne({ method: 'POST', url: '/api/painel/pedidos/o1/devolucoes' });
    expect(request.request.body).toEqual({
      items: [{ partId: 'p2', quantity: 1, restock: false }], reason: 'defect', note: null, refundAmount: 50, refundMethod: 'pix',
    });
    request.flush({ ...order, status: 'returned' });
    await settle();
    expect(done.length).toBe(1);
    backend.verify();
  });

  it('mostra a recusa da API', async () => {
    const { element, settle, set, backend } = await setup();
    await set('tbody tr:first-child input[type=number]', '1');
    await set('select[name=reason]', 'regret', 'change');
    element.querySelector<HTMLButtonElement>('button[type=submit]')!.click();

    backend.expectOne('/api/painel/pedidos/o1/devolucoes').flush({ title: 'K-1: só 0 unidade(s) podem ser devolvidas.' }, { status: 400, statusText: 'Bad Request' });
    await settle();
    expect(element.querySelector('[role=alert]')!.textContent).toContain('só 0 unidade(s)');
    backend.verify();
  });
});
