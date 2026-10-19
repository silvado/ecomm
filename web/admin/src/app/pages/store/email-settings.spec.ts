import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { EmailSettingsSection, StoreEmailSettings } from './email-settings';
import { FEEDBACK } from '../../core/feedback';

const initial: StoreEmailSettings = {
  contactEmail: null,
  usesOwnSmtp: false,
  host: null,
  port: null,
  security: null,
  user: null,
  senderName: null,
  senderAddress: null,
  hasPassword: false,
  platformSender: 'nao-responda@plataforma.test',
};

async function settle(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }) {
  await fixture.whenStable();
  fixture.detectChanges();
  await fixture.whenStable();
}

function type(element: HTMLElement, selector: string, value: string): void {
  const input = element.querySelector<HTMLInputElement>(selector)!;
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

describe('EmailSettingsSection', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] }));

  async function render(settings = initial) {
    const fixture = TestBed.createComponent(EmailSettingsSection);
    fixture.detectChanges();
    const backend = TestBed.inject(HttpTestingController);
    backend.expectOne('/api/painel/loja/email').flush(settings);
    await settle(fixture);
    return { fixture, backend, element: fixture.nativeElement as HTMLElement };
  }

  it('sem SMTP próprio mostra o remetente da plataforma e salva só o contato', async () => {
    const { fixture, backend, element } = await render();
    expect(element.textContent).toContain('nao-responda@plataforma.test');

    type(element, '[data-contact]', ' contato@loja.test ');
    await settle(fixture);
    element.querySelector<HTMLButtonElement>('[data-save-email]')!.click();
    const save = backend.expectOne({ method: 'PUT', url: '/api/painel/loja/email' });
    expect(save.request.body).toEqual({ contactEmail: 'contato@loja.test', useOwnSmtp: false, smtp: null, password: null });
    save.flush({ ...initial, contactEmail: 'contato@loja.test' });
    await settle(fixture);
    expect(save.request.context.get(FEEDBACK)).toEqual({ success: 'E-mail de envio salvo.' });
    backend.verify();
  });

  it('SMTP próprio envia o servidor e a senha, e a senha some do formulário depois de salvar', async () => {
    const { fixture, backend, element } = await render();
    element.querySelector<HTMLInputElement>('[data-own-smtp]')!.click();
    await settle(fixture);
    type(element, '[data-host]', 'smtp.gmail.com');
    type(element, '[data-user]', 'contato@loja.test');
    type(element, '[data-password]', 'senha-de-app');
    type(element, '[data-sender-name]', 'Loja');
    type(element, '[data-sender-address]', 'contato@loja.test');
    await settle(fixture);
    element.querySelector<HTMLButtonElement>('[data-save-email]')!.click();

    const save = backend.expectOne({ method: 'PUT', url: '/api/painel/loja/email' });
    expect(save.request.body.smtp).toEqual({
      host: 'smtp.gmail.com', port: 587, security: 'startTls', user: 'contato@loja.test', senderName: 'Loja', senderAddress: 'contato@loja.test',
    });
    expect(save.request.body.password).toBe('senha-de-app');
    save.flush({ ...initial, usesOwnSmtp: true, host: 'smtp.gmail.com', port: 587, security: 'startTls', user: 'contato@loja.test',
      senderName: 'Loja', senderAddress: 'contato@loja.test', hasPassword: true });
    await settle(fixture);
    expect(element.querySelector<HTMLInputElement>('[data-password]')!.value).toBe('');
    expect(element.querySelector<HTMLInputElement>('[data-password]')!.placeholder).toContain('Salva');
  });

  it('teste com falha mostra a mensagem do servidor', async () => {
    const { fixture, backend, element } = await render();
    type(element, '[data-test-to]', 'dono@loja.test');
    await settle(fixture);
    element.querySelector<HTMLButtonElement>('[data-send-test]')!.click();
    const test = backend.expectOne({ method: 'POST', url: '/api/painel/loja/email/teste' });
    expect(test.request.body).toEqual({ destinatario: 'dono@loja.test' });
    test.flush({ outcome: 'failed', message: 'O servidor smtp.gmail.com recusou o usuário ou a senha do e-mail.' });
    await settle(fixture);
    expect(element.textContent).toContain('recusou o usuário ou a senha');
  });
});
