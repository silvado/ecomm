import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { MyOrdersPage } from './my-orders';
import { StoreContext } from './store';

describe('MyOrdersPage', () => {
  const token = 'A'.repeat(43);

  async function render(fragment: string | null) {
    TestBed.configureTestingModule({
      imports: [MyOrdersPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { fragment } } },
        { provide: StoreContext, useValue: { store: { name: 'Loja Demo' }, origin: 'http://demo.localhost' } },
      ],
    });
    const fixture = TestBed.createComponent(MyOrdersPage);
    fixture.detectChanges();
    TestBed.tick();
    return { fixture, element: fixture.nativeElement as HTMLElement, backend: TestBed.inject(HttpTestingController) };
  }

  async function settle(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }) {
    for (let i = 0; i < 3; i++) {
      await fixture.whenStable();
      await Promise.resolve();
    }
    fixture.detectChanges();
  }

  it('sem sessão mostra o formulário e pede o link pelo e-mail', async () => {
    const { fixture, element, backend } = await render(null);
    backend.expectOne('/api/loja/cliente/pedidos').flush(null, { status: 401, statusText: 'Unauthorized' });
    await settle(fixture);

    const input = element.querySelector('input[type=email]') as HTMLInputElement;
    input.value = 'maria@exemplo.com.br';
    input.dispatchEvent(new Event('input'));
    (element.querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    const request = backend.expectOne({ method: 'POST', url: '/api/loja/cliente/acesso' });
    expect(request.request.body).toEqual({ email: 'maria@exemplo.com.br' });
    request.flush(null, { status: 202, statusText: 'Accepted' });
    await settle(fixture);

    expect(element.querySelector('[role=status]')!.textContent).toContain('maria@exemplo.com.br');
    backend.verify();
  });

  it('link do e-mail abre a sessão e lista os pedidos', async () => {
    const { fixture, element, backend } = await render(token);
    const session = backend.expectOne({ method: 'POST', url: '/api/loja/cliente/sessao' });
    expect(session.request.body).toEqual({ token });
    session.flush({ email: 'maria@exemplo.com.br' });
    await settle(fixture);
    backend.expectOne('/api/loja/cliente/pedidos').flush({
      email: 'maria@exemplo.com.br',
      orders: [{ number: 1001, placedAt: '2026-10-11T12:00:00Z', status: 'shipped', deliveryMethod: 'shipping', itemCount: 2, total: 140, trackingCode: 'BR1' }],
    });
    await settle(fixture);

    const order = element.querySelector('.customer-order')!;
    expect(order.textContent).toContain('Pedido nº 1001');
    expect(order.textContent).toContain('Enviado');
    expect(order.textContent).toContain('2 peças');
    backend.verify();
  });

  it('link vencido avisa e volta ao formulário', async () => {
    const { fixture, element, backend } = await render(token);
    backend.expectOne('/api/loja/cliente/sessao').flush({ title: 'x' }, { status: 400, statusText: 'Bad Request' });
    await settle(fixture);
    backend.expectOne('/api/loja/cliente/pedidos').flush(null, { status: 401, statusText: 'Unauthorized' });
    await settle(fixture);

    expect(element.querySelector('.problem')!.textContent).toContain('venceu ou já foi usado');
    expect(element.querySelector('form')).not.toBeNull();
  });
});
