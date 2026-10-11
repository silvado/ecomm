import { registerLocaleData } from '@angular/common';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import localePt from '@angular/common/locales/pt';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { availableActions, OrderPage, OrderView } from './orders.api';
import { OrdersPage } from './orders';

registerLocaleData(localePt, 'pt-BR');

describe('OrdersPage', () => {
  const order = (extra: Partial<OrderView> = {}): OrderView => ({
    id: 'o1',
    number: 1001,
    origin: 'site',
    externalId: null,
    status: 'paid',
    placedAt: '2026-10-11T12:00:00Z',
    paymentDeadline: '2026-10-12T12:00:00Z',
    buyer: { name: 'Maria da Silva', email: 'maria@exemplo.com.br', phone: '48999991234', cpf: '52998224725', cnpj: null },
    deliveryMethod: 'shipping',
    address: { postalCode: '88015200', street: 'Rua Felipe Schmidt', number: '100', complement: null, district: 'Centro', city: 'Florianópolis', state: 'SC' },
    carrier: 'Correios',
    service: 'PAC',
    deliveryDays: 5,
    pickupAddress: null,
    items: [{ partId: 'p1', internalCode: 'K-1', title: 'Farol', unitPrice: 120, quantity: 1 }],
    itemsTotal: 120,
    shippingTotal: 20,
    total: 140,
    canceledAt: null,
    cancelReason: null,
    canceledBy: null,
    paidAt: '2026-10-11T12:05:00Z',
    reviewReason: null,
    trackingCode: null,
    shippedAt: null,
    deliveredAt: null,
    paymentMethod: null,
    payments: [],
    history: [
      { at: '2026-10-11T12:00:00Z', from: null, to: 'pendingPayment', actor: 'buyer', userEmail: null, note: null },
      { at: '2026-10-11T12:05:00Z', from: 'pendingPayment', to: 'paid', actor: 'payment', userEmail: null, note: null },
    ],
    ...extra,
  });

  const pageOf = (o: OrderView): OrderPage => ({
    items: [{ id: o.id, number: o.number, origin: o.origin, status: o.status, placedAt: o.placedAt, buyerName: o.buyer.name, deliveryMethod: o.deliveryMethod, itemCount: 1, total: o.total }],
    total: 1,
    page: 1,
    pageSize: 25,
  });

  async function setup(url = '/pedidos', detail = order()) {
    TestBed.configureTestingModule({
      imports: [OrdersPage],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([{ path: 'pedidos', component: OrdersPage }])],
    });
    await TestBed.inject(Router).navigateByUrl(url);
    const fixture = TestBed.createComponent(OrdersPage);
    fixture.detectChanges();
    const backend = TestBed.inject(HttpTestingController);
    const search = backend.expectOne((r) => r.method === 'GET' && r.url === '/api/painel/pedidos');
    const params = search.request.params;
    search.flush(pageOf(detail));
    await fixture.whenStable();
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;

    const openDetail = async () => {
      element.querySelector<HTMLButtonElement>('tbody button.link')!.click();
      backend.expectOne({ method: 'GET', url: `/api/painel/pedidos/${detail.id}` }).flush(detail);
      await fixture.whenStable();
      fixture.detectChanges();
    };
    return { fixture, backend, element, params, openDetail };
  }

  const actionButtons = (element: HTMLElement) => Array.from(element.querySelectorAll<HTMLButtonElement>('[data-action]')).map((b) => b.dataset['action']);

  it('mostra as ações que cabem em cada situação', () => {
    expect(availableActions('needsReview', 'shipping')).toEqual(['aprovar', 'cancelar']);
    expect(availableActions('paid', 'shipping')).toEqual(['enviar', 'cancelar']);
    expect(availableActions('paid', 'pickup')).toEqual(['retirar', 'cancelar']);
    expect(availableActions('shipped', 'shipping')).toEqual(['entregar', 'cancelar']);
    expect(availableActions('pendingPayment', 'pickup')).toEqual(['cancelar']);
    for (const s of ['delivered', 'pickedUp', 'canceled'] as const) expect(availableActions(s, 'shipping')).toEqual([]);
    expect(availableActions('pickedUp', 'pickup', 'counter')).toEqual(['cancelar']);
    expect(availableActions('canceled', 'pickup', 'counter')).toEqual([]);
  });

  it('lê os filtros da URL e manda à API', async () => {
    const { backend, params } = await setup('/pedidos?situacao=shipped,needsReview,xyz&origem=site&de=2026-10-01&ate=2026-10-11&busca=maria');
    expect(params.get('situacao')).toBe('shipped,needsReview');
    expect(params.get('origem')).toBe('site');
    expect(params.get('de')).toBe('2026-10-01');
    expect(params.get('ate')).toBe('2026-10-11');
    expect(params.get('busca')).toBe('maria');
    backend.verify();
  });

  it('marcar situações filtra por várias de uma vez e guarda na URL', async () => {
    const { fixture, backend, element } = await setup();
    element.querySelector<HTMLInputElement>('[data-status="delivered"]')!.click();
    backend.expectOne((r) => r.params.get('situacao') === 'delivered').flush(pageOf(order()));
    element.querySelector<HTMLInputElement>('[data-status="needsReview"]')!.click();
    backend.expectOne((r) => r.params.get('situacao') === 'needsReview,delivered').flush(pageOf(order()));
    await fixture.whenStable();
    expect(TestBed.inject(Router).url).toContain('situacao=needsReview,delivered');
    backend.verify();
  });

  it('enviar pede confirmação e manda o rastreio sem espaços', async () => {
    const { fixture, backend, element, openDetail } = await setup();
    await openDetail();
    expect(actionButtons(element)).toEqual(['enviar', 'cancelar']);

    element.querySelector<HTMLButtonElement>('[data-action="enviar"]')!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    backend.expectNone(() => true);
    const input = element.querySelector<HTMLInputElement>('[data-confirm] input[name="rastreio"]')!;
    input.value = '  BR123456789BR ';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    element.querySelector<HTMLButtonElement>('[data-confirm] button[type=submit]')!.click();

    const act = backend.expectOne({ method: 'POST', url: '/api/painel/pedidos/o1/enviar' });
    expect(act.request.body).toEqual({ rastreio: 'BR123456789BR' });
    act.flush(order({ status: 'shipped', trackingCode: 'BR123456789BR', shippedAt: '2026-10-11T13:00:00Z' }));
    await fixture.whenStable();
    fixture.detectChanges();

    expect(element.querySelector('h2')!.textContent).toContain('Enviado');
    expect(element.textContent).toContain('rastreio BR123456789BR');
    expect(actionButtons(element)).toEqual(['entregar', 'cancelar']);
    expect(element.querySelector('tbody .badge')!.textContent).toContain('Enviado');
    backend.verify();
  });

  it('cancelar exige motivo e mostra a recusa da API', async () => {
    const { fixture, backend, element, openDetail } = await setup();
    await openDetail();
    element.querySelector<HTMLButtonElement>('[data-action="cancelar"]')!.click();
    fixture.detectChanges();
    await fixture.whenStable();

    const submit = element.querySelector<HTMLButtonElement>('[data-confirm] button[type=submit]')!;
    expect(element.querySelector('[data-confirm]')!.textContent).toContain('voltam ao estoque');
    expect(submit.disabled).toBe(true);

    const reason = element.querySelector<HTMLTextAreaElement>('[data-confirm] textarea')!;
    reason.value = ' Peça com defeito ';
    reason.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(submit.disabled).toBe(false);
    submit.click();

    const act = backend.expectOne({ method: 'POST', url: '/api/painel/pedidos/o1/cancelar' });
    expect(act.request.body).toEqual({ motivo: 'Peça com defeito' });
    act.flush({ title: 'O pedido não pode mais ser cancelado.' }, { status: 409, statusText: 'Conflict' });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(element.querySelector('[role=alert]')!.textContent).toContain('não pode mais ser cancelado');
    expect(element.querySelector('[data-confirm]')).not.toBeNull();
    backend.verify();
  });

  it('histórico mostra de → para, quem fez e a observação', async () => {
    const detail = order({
      status: 'canceled',
      canceledBy: 'store',
      canceledAt: '2026-10-11T14:00:00Z',
      cancelReason: 'Peça com defeito',
      history: [
        ...order().history,
        { at: '2026-10-11T14:00:00Z', from: 'paid', to: 'canceled', actor: 'user', userEmail: 'dono@demo.local', note: 'Peça com defeito' },
      ],
    });
    const { element, openDetail, backend } = await setup('/pedidos', detail);
    await openDetail();

    const lines = Array.from(element.querySelectorAll('ol.history li')).map((li) => li.textContent!.replace(/\s+/g, ' '));
    expect(lines.length).toBe(3);
    expect(lines[0]).toContain('Aguardando pagamento · Comprador');
    expect(lines[2]).toContain('Pago → Cancelado · dono@demo.local');
    expect(lines[2]).toContain('Peça com defeito');
    expect(element.textContent).toContain('Cancelado pela loja');
    expect(actionButtons(element)).toEqual([]);
    backend.verify();
  });
});
