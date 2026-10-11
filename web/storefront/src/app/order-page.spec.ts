import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { StoreOrder } from './checkout';
import { OrderPage } from './order-page';
import { StoreContext } from './store';

describe('OrderPage', () => {
  const order = (extra: Partial<StoreOrder> = {}): StoreOrder => ({
    number: 1001,
    status: 'paid',
    placedAt: '2026-10-11T12:00:00Z',
    paymentDeadline: '2026-10-12T12:00:00Z',
    buyerName: 'Maria da Silva',
    deliveryMethod: 'shipping',
    address: { postalCode: '88015200', street: 'Rua Felipe Schmidt', number: '100', complement: null, district: 'Centro', city: 'Florianópolis', state: 'SC' },
    carrier: 'Correios',
    service: 'PAC',
    deliveryDays: 5,
    pickupAddress: null,
    items: [{ partId: 'p1', title: 'Farol', unitPrice: 120, quantity: 1 }],
    itemsTotal: 120,
    shippingTotal: 20,
    total: 140,
    cancelReason: null,
    trackingCode: null,
    shippedAt: null,
    deliveredAt: null,
    ...extra,
  });

  async function render(o: StoreOrder) {
    TestBed.configureTestingModule({
      imports: [OrderPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { fragment: 'token-do-pedido' } } },
        { provide: StoreContext, useValue: { store: { name: 'Loja Demo' }, origin: 'http://demo.localhost' } },
      ],
    });
    const fixture = TestBed.createComponent(OrderPage);
    fixture.detectChanges();
    TestBed.tick(); // afterNextRender: busca o pedido depois da "hidratação"
    const backend = TestBed.inject(HttpTestingController);
    const find = backend.expectOne({ method: 'POST', url: '/api/loja/pedidos/consulta' });
    expect(find.request.body).toEqual({ token: 'token-do-pedido' });
    find.flush(o);
    await fixture.whenStable();
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const steps = Array.from(element.querySelectorAll('.order-steps li')).map((li) => ({
      label: li.querySelector('span')!.textContent!.trim(),
      done: li.classList.contains('done'),
      current: li.getAttribute('aria-current') === 'step',
    }));
    return { element, steps, backend };
  }

  it('enviado mostra a etapa atual e o código de rastreio', async () => {
    const { element, steps, backend } = await render(order({ status: 'shipped', trackingCode: 'BR123456789BR', shippedAt: '2026-10-11T15:00:00Z' }));

    expect(steps.map((s) => s.label)).toEqual(['Pedido feito', 'Pago', 'Enviado', 'Entregue']);
    expect(steps.map((s) => s.done)).toEqual([true, true, true, false]);
    expect(steps.find((s) => s.current)!.label).toBe('Enviado');
    expect(element.querySelector('.tracking')!.textContent).toBe('BR123456789BR');
    backend.verify();
  });

  it('retirada tem a etapa Retirado no lugar de Enviado e Entregue', async () => {
    const { element, steps } = await render(
      order({ status: 'pickedUp', deliveryMethod: 'pickup', address: null, pickupAddress: 'Balcão da loja', deliveredAt: '2026-10-11T16:00:00Z' }),
    );

    expect(steps.map((s) => s.label)).toEqual(['Pedido feito', 'Pago', 'Retirado']);
    expect(steps.every((s) => s.done)).toBe(true);
    expect(element.textContent).toContain('Peças retiradas na loja');
  });

  it('cancelado mostra só o aviso com o motivo, sem etapas', async () => {
    const { element, steps } = await render(order({ status: 'canceled', cancelReason: 'Peça com defeito' }));

    expect(steps).toEqual([]);
    expect(element.querySelector('.notice')!.textContent).toContain('Peça com defeito');
  });
});
