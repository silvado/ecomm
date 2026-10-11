import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MercadoPagoSettings } from './payments.api';
import { PaymentsPage } from './payments';

describe('PaymentsPage', () => {
  const url = 'https://loja.plataforma.test/api/webhooks/pagamentos/mercado-pago/t1';
  const empty: MercadoPagoSettings = {
    configured: false,
    accessTokenHint: null,
    webhookSecretHint: null,
    updatedAt: null,
    notificationUrl: url,
  };

  async function setup() {
    TestBed.configureTestingModule({
      imports: [PaymentsPage],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(PaymentsPage);
    fixture.detectChanges();
    const backend = TestBed.inject(HttpTestingController);
    backend.expectOne({ method: 'GET', url: '/api/painel/pagamentos/mercado-pago' }).flush(empty);
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, backend, element: fixture.nativeElement as HTMLElement };
  }

  it('mostra a URL de notificação da loja e envia as chaves sem espaços, limpando o formulário', async () => {
    const { fixture, backend, element } = await setup();
    expect(element.textContent).toContain('Sem pagamento online');
    expect(element.querySelector('code')!.textContent).toBe(url);

    const [token, secret] = Array.from(element.querySelectorAll<HTMLInputElement>('input'));
    expect(token.type).toBe('password');
    token.value = '  APP_USR-1234567890abcdef-wxyz ';
    token.dispatchEvent(new Event('input'));
    secret.value = 'segredo-do-webhook-1234';
    secret.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    element.querySelector<HTMLButtonElement>('button[type=submit]')!.click();

    const save = backend.expectOne({ method: 'PUT', url: '/api/painel/pagamentos/mercado-pago' });
    expect(save.request.body).toEqual({ accessToken: 'APP_USR-1234567890abcdef-wxyz', webhookSecret: 'segredo-do-webhook-1234' });
    save.flush({ ...empty, configured: true, accessTokenHint: 'wxyz', webhookSecretHint: '1234', updatedAt: '2026-10-11T12:00:00Z' });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(element.textContent).toContain('Mercado Pago conectado');
    expect(element.textContent).toContain('wxyz');
    expect(Array.from(element.querySelectorAll('input')).map((i) => i.value)).toEqual(['', '']);
    backend.verify();
  });

  it('não envia chave curta demais', async () => {
    const { fixture, backend, element } = await setup();
    const [token, secret] = Array.from(element.querySelectorAll<HTMLInputElement>('input'));
    token.value = 'curto';
    token.dispatchEvent(new Event('input'));
    secret.value = 'segredo-do-webhook-1234';
    secret.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    expect(element.querySelector<HTMLButtonElement>('button[type=submit]')!.disabled).toBe(true);
    backend.verify();
  });
});
