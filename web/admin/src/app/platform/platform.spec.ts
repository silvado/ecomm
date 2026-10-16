import { registerLocaleData } from '@angular/common';
import localePt from '@angular/common/locales/pt';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { LOCALE_ID } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthStore } from '../core/auth';
import { authInterceptor } from '../core/auth.interceptor';
import { PlatformAuthStore, PlatformStep, platformInterceptor } from './platform-auth';
import { groupKey, PlatformLoginPage } from './platform-login';
import { PlatformTenantDetail } from './platform.api';
import { PlatformTenantPage } from './tenant-detail';

registerLocaleData(localePt, 'pt-BR');

const step = (overrides: Partial<PlatformStep> = {}): PlatformStep => ({
  stage: 'completo',
  accessToken: 'plataforma-1',
  expiresAt: '2026-10-15T12:15:00Z',
  admin: { email: 'sa@plataforma.test', mustChangePassword: false, hasMfa: true },
  ...overrides,
});

const detail: PlatformTenantDetail = {
  id: 't1',
  slug: 'pecas',
  tradeName: 'Peças do João',
  legalName: 'Peças do João Ltda',
  cnpj: '11222333000181',
  status: 'Active',
  planCode: 'essencial',
  planName: 'Essencial',
  createdAt: '2026-10-01T12:00:00Z',
  customStorefrontSince: null,
  domains: [{ host: 'pecas.plataforma.test', kind: 'PlatformSubdomain', status: 'Verified', isPrimary: true }],
  users: [{ email: 'dono@pecas.test', role: 'Owner', mustChangePassword: false, lockedOut: false }],
  billingHold: false,
};

async function settle(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }) {
  await fixture.whenStable();
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

describe('Sessão da plataforma', () => {
  let backend: HttpTestingController;
  let http: HttpClient;
  let platform: PlatformAuthStore;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([authInterceptor, platformInterceptor])), provideHttpClientTesting()],
    });
    backend = TestBed.inject(HttpTestingController);
    http = TestBed.inject(HttpClient);
    platform = TestBed.inject(PlatformAuthStore);
  });

  afterEach(() => backend.verify());

  it('manda o token da plataforma só para /api/plataforma e nunca o da loja', async () => {
    const login = platform.login('sa@plataforma.test', 'senha');
    const entrar = backend.expectOne('/api/plataforma/auth/entrar');
    expect(entrar.request.headers.has('Authorization')).toBe(false);
    entrar.flush(step({ stage: 'codigo', accessToken: 'etapa-1' }));
    await login;

    const code = platform.verifyCode('123456');
    const codigo = backend.expectOne('/api/plataforma/auth/codigo');
    expect(codigo.request.headers.get('Authorization')).toBe('Bearer etapa-1');
    codigo.flush(step());
    await code;

    const list = firstValueFrom(http.get('/api/plataforma/lojas'));
    const lojas = backend.expectOne('/api/plataforma/lojas');
    expect(lojas.request.headers.get('Authorization')).toBe('Bearer plataforma-1');
    lojas.flush([]);
    await list;

    // Sem sessão de loja, o painel de loja não recebe o token da plataforma.
    const panel = firstValueFrom(http.get('/api/painel/eu'));
    const eu = backend.expectOne('/api/painel/eu');
    expect(eu.request.headers.has('Authorization')).toBe(false);
    eu.flush({});
    await panel;
  });

  it('token de loja não vai para /api/plataforma', async () => {
    const auth = TestBed.inject(AuthStore);
    const login = auth.login('dono@pecas.test', 'senha');
    backend.expectOne('/api/auth/login').flush({ accessToken: 'loja-1', tenant: null, tenants: [], mustChangePassword: false });
    await login;

    const list = firstValueFrom(http.get('/api/plataforma/lojas'));
    const lojas = backend.expectOne('/api/plataforma/lojas');
    expect(lojas.request.headers.has('Authorization')).toBe(false);
    lojas.flush([]);
    await list;
  });

  it('401 numa rota da plataforma renova pelo cookie e repete; sem sessão, desiste', async () => {
    const first = firstValueFrom(http.get('/api/plataforma/lojas'));
    backend.expectOne('/api/plataforma/lojas').flush(null, { status: 401, statusText: 'Unauthorized' });
    await Promise.resolve();
    backend.expectOne('/api/plataforma/auth/renovar').flush(step({ accessToken: 'renovado' }));
    await new Promise((resolve) => setTimeout(resolve));
    const retry = backend.expectOne('/api/plataforma/lojas');
    expect(retry.request.headers.get('Authorization')).toBe('Bearer renovado');
    retry.flush([]);
    await first;

    platform.reset();
    const second = firstValueFrom(http.get('/api/plataforma/planos'));
    backend.expectOne('/api/plataforma/planos').flush(null, { status: 401, statusText: 'Unauthorized' });
    await Promise.resolve();
    backend.expectOne('/api/plataforma/auth/renovar').flush(null, { status: 401, statusText: 'Unauthorized' });
    await expect(second).rejects.toBeTruthy();
  });

  it('agrupa a chave do app em blocos de 4', () => {
    expect(groupKey('ABCDEFGHIJKLMNOPQRSTUVWXYZ234567')).toBe('ABCD EFGH IJKL MNOP QRST UVWX YZ23 4567');
  });
});

describe('PlatformLoginPage', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({ providers: [provideHttpClient(withInterceptors([platformInterceptor])), provideHttpClientTesting(), provideRouter([])] }),
  );

  function type(element: HTMLElement, selector: string, value: string) {
    const input = element.querySelector<HTMLInputElement>(selector)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  it('primeiro acesso: troca a senha, cadastra o app e mostra os códigos de recuperação uma vez', async () => {
    const fixture = TestBed.createComponent(PlatformLoginPage);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const backend = TestBed.inject(HttpTestingController);

    type(element, 'input[formControlName=email]', 'sa@plataforma.test');
    type(element, 'input[formControlName=password]', 'provisoria-123');
    fixture.detectChanges();
    element.querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit'));
    backend.expectOne('/api/plataforma/auth/entrar').flush(
      step({ stage: 'cadastro', accessToken: 'etapa', admin: { email: 'sa@plataforma.test', mustChangePassword: true, hasMfa: false } }),
    );
    await settle(fixture);
    expect(element.textContent).toContain('troque a senha provisória');

    type(element, 'input[formControlName=currentPassword]', 'provisoria-123');
    type(element, 'input[formControlName=newPassword]', 'senha-nova-1234');
    type(element, 'input[formControlName=confirmation]', 'senha-nova-1234');
    fixture.detectChanges();
    element.querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit'));
    backend.expectOne('/api/plataforma/auth/senha').flush(
      step({ stage: 'cadastro', accessToken: 'etapa-2', admin: { email: 'sa@plataforma.test', mustChangePassword: false, hasMfa: false } }),
    );
    await settle(fixture);

    element.querySelector<HTMLButtonElement>('main button')!.click();
    backend.expectOne('/api/plataforma/auth/mfa').flush({ secret: 'ABCDEFGHIJKLMNOP', provisioningUri: 'otpauth://totp/x' });
    await settle(fixture);
    expect(element.querySelector('[data-secret]')!.textContent).toBe('ABCD EFGH IJKL MNOP');

    type(element, '[data-code]', '123456');
    fixture.detectChanges();
    element.querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit'));
    const confirm = backend.expectOne('/api/plataforma/auth/mfa/confirmar');
    expect(confirm.request.headers.get('Authorization')).toBe('Bearer etapa-2');
    confirm.flush(step({ recoveryCodes: ['AAAAA-BBBBB', 'CCCCC-DDDDD'] }));
    await settle(fixture);

    expect(element.querySelectorAll('[data-recovery] li').length).toBe(2);
    // Os códigos não ficam guardados na sessão.
    expect(TestBed.inject(PlatformAuthStore).current()?.recoveryCodes).toBeNull();
  });

  it('com MFA ativo pede o código; código errado mostra a mensagem da API', async () => {
    const fixture = TestBed.createComponent(PlatformLoginPage);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const backend = TestBed.inject(HttpTestingController);

    type(element, 'input[formControlName=email]', 'sa@plataforma.test');
    type(element, 'input[formControlName=password]', 'senha-boa-123');
    fixture.detectChanges();
    element.querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit'));
    backend.expectOne('/api/plataforma/auth/entrar').flush(step({ stage: 'codigo', accessToken: 'etapa' }));
    await settle(fixture);

    type(element, '[data-code]', '000000');
    fixture.detectChanges();
    element.querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit'));
    backend.expectOne('/api/plataforma/auth/codigo').flush({ title: 'Código inválido ou já usado.' }, { status: 401, statusText: 'Unauthorized' });
    await settle(fixture);

    expect(element.querySelector('[role=alert]')!.textContent).toContain('Código inválido');
  });
});

describe('PlatformTenantPage', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), { provide: LOCALE_ID, useValue: 'pt-BR' }],
    }),
  );

  it('suspender pede confirmação e mostra a loja suspensa com a opção de reativar', async () => {
    const fixture = TestBed.createComponent(PlatformTenantPage);
    fixture.componentRef.setInput('id', 't1');
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const backend = TestBed.inject(HttpTestingController);
    backend.expectOne('/api/plataforma/lojas/t1').flush(detail);
    backend.expectOne('/api/plataforma/planos').flush([
      { code: 'essencial', name: 'Essencial', limits: {}, features: [], tenants: 1 },
      { code: 'profissional', name: 'Profissional', limits: {}, features: [], tenants: 0 },
    ]);
    await settle(fixture);
    expect(element.querySelector('[data-status]')!.textContent).toBe('Ativa');
    expect(element.textContent).toContain('Dono');

    element.querySelector<HTMLButtonElement>('[data-suspend]')!.click();
    fixture.detectChanges();
    backend.expectNone('/api/plataforma/lojas/t1/suspender');
    element.querySelector<HTMLButtonElement>('[data-confirm-suspend]')!.click();
    backend.expectOne('/api/plataforma/lojas/t1/suspender').flush({ ...detail, status: 'Suspended' });
    await settle(fixture);

    expect(element.querySelector('[data-status]')!.textContent).toBe('Suspensa');
    expect(element.querySelector('[data-reactivate]')).not.toBeNull();
  });
});
