import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { AuthStore, Session } from './auth';
import { authInterceptor } from './auth.interceptor';
import { landingFor } from './guards';

const session = (overrides: Partial<Session> = {}): Session => ({
  accessToken: 'token-1',
  expiresAt: '2026-10-08T12:15:00Z',
  userId: 'u1',
  email: 'dono@pecas.test',
  mustChangePassword: false,
  tenant: { id: 't1', slug: 'loja', name: 'Loja', role: 'owner', permissions: ['usersManage', 'vaultManage'] },
  tenants: [],
  ...overrides,
});

describe('AuthStore e interceptor', () => {
  let auth: AuthStore;
  let http: HttpClient;
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()],
    });
    auth = TestBed.inject(AuthStore);
    http = TestBed.inject(HttpClient);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

  it('guarda a sessão do login e libera as permissões do perfil', async () => {
    const login = auth.login('dono@pecas.test', 'senha');
    const request = backend.expectOne('/api/auth/login');
    expect(request.request.headers.has('Authorization')).toBe(false);
    request.flush(session());
    await login;

    expect(auth.accessToken()).toBe('token-1');
    expect(auth.can('usersManage')).toBe(true);
    expect(auth.can('dataExport')).toBe(false);
  });

  it('envia o token e, quando ele expira, renova pelo cookie e repete a requisição', async () => {
    const login = auth.login('dono@pecas.test', 'senha');
    backend.expectOne('/api/auth/login').flush(session());
    await login;

    const call = firstValueFrom(http.get('/api/painel/eu'));
    const first = backend.expectOne('/api/painel/eu');
    expect(first.request.headers.get('Authorization')).toBe('Bearer token-1');
    first.flush(null, { status: 401, statusText: 'Unauthorized' });

    await vi.waitFor(() => backend.expectOne('/api/auth/refresh').flush(session({ accessToken: 'token-2' })));
    await vi.waitFor(() => {
      const retry = backend.expectOne('/api/painel/eu');
      expect(retry.request.headers.get('Authorization')).toBe('Bearer token-2');
      retry.flush({ ok: true });
    });
    expect(await call).toEqual({ ok: true });
  });

  it('renovação recusada encerra a sessão', async () => {
    const refresh = auth.refresh();
    backend.expectOne('/api/auth/refresh').flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(await refresh).toBe(false);
    expect(auth.current()).toBeNull();
  });

  it('outra aba renovou ao mesmo tempo (409): espera e tenta de novo', async () => {
    vi.useFakeTimers();
    try {
      const refresh = auth.refresh();
      backend.expectOne('/api/auth/refresh').flush(null, { status: 409, statusText: 'Conflict' });
      await vi.advanceTimersByTimeAsync(300);
      backend.expectOne('/api/auth/refresh').flush(session());

      expect(await refresh).toBe(true);
      expect(auth.tenant()?.id).toBe('t1');
    } finally {
      vi.useRealTimers();
    }
  });

  it('renovações pedidas juntas viram uma só chamada', async () => {
    const first = auth.refresh();
    const second = auth.refresh();
    backend.expectOne('/api/auth/refresh').flush(session());

    expect(await first).toBe(true);
    expect(await second).toBe(true);
  });
});

describe('landingFor', () => {
  it('senha provisória vai para a troca de senha', () =>
    expect(landingFor(session({ mustChangePassword: true }))).toBe('/trocar-senha'));

  it('sem loja escolhida vai para a escolha', () => expect(landingFor(session({ tenant: null }))).toBe('/escolher-loja'));

  it('com loja vai para o painel', () => expect(landingFor(session())).toBe('/'));
});
