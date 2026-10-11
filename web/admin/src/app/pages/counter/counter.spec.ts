import { registerLocaleData } from '@angular/common';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import localePt from '@angular/common/locales/pt';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { PartPage, PartSummary } from '../parts/parts.api';
import { CounterPage, newSaleToken } from './counter';

registerLocaleData(localePt, 'pt-BR');

describe('CounterPage', () => {
  const part = (id: string, available: number, price = 120): PartSummary => ({
    id,
    internalCode: `K-${id}`,
    title: `Peça ${id}`,
    condition: 'used',
    price,
    status: 'active',
    stock: { onHand: available, reserved: 0, available },
    hasShippingDimensions: true,
    updatedAt: '2026-10-11T12:00:00Z',
    coverPhotoId: null,
  });
  const pageOf = (...items: PartSummary[]): PartPage => ({ items, total: items.length, page: 1, pageSize: 10 });

  async function setup() {
    TestBed.configureTestingModule({ imports: [CounterPage], providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    const fixture = TestBed.createComponent(CounterPage);
    fixture.detectChanges();
    await fixture.whenStable();
    const backend = TestBed.inject(HttpTestingController);
    const element = fixture.nativeElement as HTMLElement;

    const type = async (selector: string, value: string) => {
      const input = element.querySelector<HTMLInputElement | HTMLSelectElement>(selector)!;
      input.value = value;
      input.dispatchEvent(new Event(input instanceof HTMLSelectElement ? 'change' : 'input'));
      fixture.detectChanges();
      await fixture.whenStable();
    };
    const search = async (text: string, result: PartPage) => {
      await type('input[type=search]', text);
      await new Promise((resolve) => setTimeout(resolve, 350)); // espera da busca
      backend.expectOne((r) => r.url === '/api/painel/pecas' && r.params.get('busca') === text).flush(result);
      await fixture.whenStable();
      fixture.detectChanges();
      await fixture.whenStable();
    };
    return { fixture, backend, element, type, search };
  }

  it('token da venda tem 32 bytes em base64url e muda a cada venda', () => {
    const a = newSaleToken();
    expect(a).toMatch(/^[A-Za-z0-9_-]{43}$/);
    expect(newSaleToken()).not.toBe(a);
  });

  it('monta a venda com preço ajustado e envia ao balcão', async () => {
    const { fixture, backend, element, type, search } = await setup();
    const submit = () => element.querySelector<HTMLButtonElement>('button[type=submit]')!;

    await search('farol', pageOf(part('1', 3), part('2', 0)));
    expect(element.querySelector<HTMLButtonElement>('[data-add="2"]')!.disabled).toBe(true);
    element.querySelector<HTMLButtonElement>('[data-add="1"]')!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(element.querySelector<HTMLButtonElement>('[data-add="1"]')!.textContent).toContain('Na venda');
    expect(submit().disabled).toBe(true); // falta a forma de pagamento

    await type('[data-lines] tbody tr:first-child td:nth-child(2) input', '2');
    await type('[data-lines] tbody tr:first-child td:nth-child(3) input', '100');
    await type('select[name=paymentMethod]', 'pix');
    await type('input[name=document]', '11.222.333/0001-81');
    expect(element.querySelector('[data-total]')!.textContent).toContain('200,00');
    expect(element.querySelector('[data-lines]')!.textContent).toContain('Tabela');
    expect(submit().disabled).toBe(false);

    submit().click();
    const sale = backend.expectOne({ method: 'POST', url: '/api/painel/balcao' });
    expect(sale.request.body.items).toEqual([{ partId: '1', quantity: 2, unitPrice: 100 }]);
    expect(sale.request.body.paymentMethod).toBe('pix');
    expect(sale.request.body.customer).toEqual({ name: '', document: '11.222.333/0001-81', phone: '' });
    expect(sale.request.body.token).toMatch(/^[A-Za-z0-9_-]{43}$/);
    sale.flush({ id: 'o1', number: 42, total: 200 });
    await fixture.whenStable();
    backend.expectOne((r) => r.url === '/api/painel/pecas').flush(pageOf(part('1', 1)));
    await fixture.whenStable();
    fixture.detectChanges();

    expect(element.querySelector('[role=status]')!.textContent).toContain('Venda nº 42 registrada');
    expect(element.querySelector('[data-lines]')).toBeNull();
    backend.verify();
  });

  it('venda recusada por falta de estoque mostra a mensagem e atualiza o disponível', async () => {
    const { fixture, backend, element, type, search } = await setup();
    await search('farol', pageOf(part('1', 1)));
    element.querySelector<HTMLButtonElement>('[data-add="1"]')!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    await type('select[name=paymentMethod]', 'cash');

    element.querySelector<HTMLButtonElement>('button[type=submit]')!.click();
    backend.expectOne('/api/painel/balcao').flush({ title: 'Sem estoque disponível de K-1 · Peça 1 para 1 unidade(s).' }, { status: 409, statusText: 'Conflict' });
    await fixture.whenStable();
    backend.expectOne((r) => r.url === '/api/painel/pecas').flush(pageOf(part('1', 0)));
    await fixture.whenStable();
    fixture.detectChanges();

    expect(element.querySelector('[role=alert]')!.textContent).toContain('Sem estoque disponível de K-1');
    expect(element.querySelector('[data-lines]')).not.toBeNull();
    backend.verify();
  });
});
