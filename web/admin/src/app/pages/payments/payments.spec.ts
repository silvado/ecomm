import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { GatewaySettings } from './payments.api';
import { PaymentsPage } from './payments';

describe('PaymentsPage', () => {
  const settings = (gateway: 'mercado-pago' | 'cielo', extra: Partial<GatewaySettings> = {}): GatewaySettings => ({
    gateway,
    configured: false,
    active: false,
    credentials: [],
    updatedAt: null,
    notificationUrl: `https://loja.plataforma.test/api/webhooks/pagamentos/${gateway}/t1`,
    ...extra,
  });

  async function setup(list = [settings('cielo'), settings('mercado-pago')]) {
    TestBed.configureTestingModule({
      imports: [PaymentsPage],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(PaymentsPage);
    fixture.detectChanges();
    const backend = TestBed.inject(HttpTestingController);
    backend.expectOne({ method: 'GET', url: '/api/painel/pagamentos' }).flush(list);
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, backend, element: fixture.nativeElement as HTMLElement };
  }

  function type(input: HTMLInputElement, value: string): void {
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  it('mostra a URL de cada gateway e envia as chaves sem espaços, limpando o formulário', async () => {
    const { fixture, backend, element } = await setup();
    const mp = element.querySelector<HTMLElement>('[data-gateway="mercado-pago"]')!;
    expect(mp.querySelector('code')!.textContent).toBe('https://loja.plataforma.test/api/webhooks/pagamentos/mercado-pago/t1');
    expect(element.querySelector('[data-gateway="cielo"] code')!.textContent).toContain('/pagamentos/cielo/t1');

    const [token, secret] = Array.from(mp.querySelectorAll<HTMLInputElement>('input'));
    expect(token.type).toBe('password');
    type(token, '  APP_USR-1234567890abcdef-wxyz ');
    type(secret, 'segredo-do-webhook-1234');
    fixture.detectChanges();
    mp.querySelector<HTMLButtonElement>('button[type=submit]')!.click();

    const save = backend.expectOne({ method: 'PUT', url: '/api/painel/pagamentos/mercado-pago' });
    expect(save.request.body).toEqual({ accessToken: 'APP_USR-1234567890abcdef-wxyz', webhookSecret: 'segredo-do-webhook-1234' });
    save.flush([
      settings('cielo'),
      settings('mercado-pago', {
        configured: true,
        active: true,
        credentials: [
          { field: 'accessToken', hint: 'wxyz' },
          { field: 'webhookSecret', hint: '1234' },
        ],
        updatedAt: '2026-10-11T12:00:00Z',
      }),
    ]);
    await fixture.whenStable();
    fixture.detectChanges();

    expect(mp.textContent).toContain('Em uso pela loja');
    expect(mp.textContent).toContain('wxyz');
    expect(Array.from(mp.querySelectorAll('input')).map((i) => i.value)).toEqual(['', '']);
    backend.verify();
  });

  it('Cielo exige MerchantId no formato de GUID', async () => {
    const { fixture, backend, element } = await setup();
    const cielo = element.querySelector<HTMLElement>('[data-gateway="cielo"]')!;
    const [merchant, client, secret] = Array.from(cielo.querySelectorAll<HTMLInputElement>('input'));
    const submit = cielo.querySelector<HTMLButtonElement>('button[type=submit]')!;
    type(client, 'client-id-comprido-o-bastante');
    type(secret, 'client-secret-comprido-o-bastante');

    type(merchant, 'nao-e-guid');
    fixture.detectChanges();
    expect(submit.disabled).toBe(true);

    type(merchant, '4c1c5b5a-1d5f-4a3e-9a0b-6f1d2e3c4b5a');
    fixture.detectChanges();
    expect(submit.disabled).toBe(false);
    backend.verify();
  });

  it('não envia chave curta demais', async () => {
    const { fixture, backend, element } = await setup();
    const mp = element.querySelector<HTMLElement>('[data-gateway="mercado-pago"]')!;
    const [token, secret] = Array.from(mp.querySelectorAll<HTMLInputElement>('input'));
    type(token, 'curto');
    type(secret, 'segredo-do-webhook-1234');
    fixture.detectChanges();

    expect(mp.querySelector<HTMLButtonElement>('button[type=submit]')!.disabled).toBe(true);
    backend.verify();
  });
});
