import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { DOCUMENT } from '@angular/common';
import { REQUEST_CONTEXT, RESPONSE_INIT } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, convertToParamMap, provideRouter, RedirectCommand, RouterStateSnapshot } from '@angular/router';
import { firstValueFrom, isObservable, Observable } from 'rxjs';
import { apiInterceptor } from './api.interceptor';
import { partIdFromSlug, partPath, slugify, StorePartView, summarize } from './catalog';
import { partResolver } from './resolvers';
import { Seo } from './seo';
import { StoreRequestContext } from './store';

const ID = '01a12157-8e9c-7707-9681-c8df9ce7ce50';

const serverContext: StoreRequestContext = {
  store: { kind: 'not-found' },
  origin: 'https://loja.test',
  api: { url: 'http://api:8080', host: 'loja.test', proto: 'https', clientIp: '203.0.113.9' },
};

const part: StorePartView = {
  id: ID,
  internalCode: 'FAR-1',
  title: 'Farol dianteiro – Gol G5 (2009/2012)',
  description: 'Original.',
  condition: 'used',
  price: 350,
  available: 1,
  photoIds: [],
  oemCodes: [],
  compatibilities: [],
  updatedAt: '2026-10-09T12:00:00Z',
};

describe('URLs e textos da vitrine', () => {
  it('título vira trecho de URL sem acentos e símbolos', () =>
    expect(slugify('Pára-choque dianteiro – Gol G5 (2009/2012)')).toBe('para-choque-dianteiro-gol-g5-2009-2012'));

  it('endereço da peça leva o id e o título', () =>
    expect(partPath(part)).toBe(`/peca/${ID}-farol-dianteiro-gol-g5-2009-2012`));

  it('o id manda: título errado ou ausente ainda acha a peça', () => {
    expect(partIdFromSlug(`${ID}-titulo-antigo`)).toBe(ID);
    expect(partIdFromSlug(ID.toUpperCase())).toBe(ID);
    expect(partIdFromSlug('farol-gol')).toBeNull();
  });

  it('descrição curta corta em palavra inteira', () => {
    expect(summarize('Peça   original\n\nsem trincas')).toBe('Peça original sem trincas');
    const long = summarize('palavra '.repeat(40), 30);
    expect(long.length).toBeLessThanOrEqual(31);
    expect(long.endsWith('…')).toBe(true);
  });
});

describe('Seo', () => {
  it('escreve canonical, Open Graph e JSON-LD sem permitir fechar o <script>', () => {
    TestBed.configureTestingModule({});
    TestBed.inject(Seo).apply({
      title: 'Farol',
      description: 'Desc',
      path: '/peca/x',
      origin: 'https://loja.test',
      type: 'product',
      image: 'https://loja.test/f.webp',
      jsonLd: { name: '</script><script>alert(1)</script>' },
    });
    const head = TestBed.inject(DOCUMENT).head;

    expect(head.querySelector('link[rel="canonical"]')!.getAttribute('href')).toBe('https://loja.test/peca/x');
    expect(head.querySelector('meta[property="og:image"]')!.getAttribute('content')).toBe('https://loja.test/f.webp');
    const script = head.querySelector('#jsonld')!.textContent!;
    expect(script).not.toContain('</script>');
    expect(JSON.parse(script).name).toBe('</script><script>alert(1)</script>');
  });
});

describe('apiInterceptor', () => {
  it('no servidor, chama a API interna com o Host da loja; fora de /api/loja não mexe', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([apiInterceptor])),
        provideHttpClientTesting(),
        { provide: REQUEST_CONTEXT, useValue: serverContext },
      ],
    });
    const http = TestBed.inject(HttpClient);
    const backend = TestBed.inject(HttpTestingController);

    const call = firstValueFrom(http.get('/api/loja/pecas'));
    const request = backend.expectOne('http://api:8080/api/loja/pecas');
    expect(request.request.headers.get('X-Forwarded-Host')).toBe('loja.test');
    expect(request.request.headers.get('X-Forwarded-For')).toBe('203.0.113.9');
    request.flush({});
    await call;

    void firstValueFrom(http.get('/assets/x.json'));
    backend.expectOne('/assets/x.json').flush({});
  });
});

describe('partResolver', () => {
  function resolve(slug: string, response: ResponseInit) {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting(), { provide: RESPONSE_INIT, useValue: response }],
    });
    const route = { paramMap: convertToParamMap({ slug }) } as ActivatedRouteSnapshot;
    const result = TestBed.runInInjectionContext(() => partResolver(route, {} as RouterStateSnapshot));
    return { result, backend: TestBed.inject(HttpTestingController) };
  }

  it('peça inexistente responde 404', async () => {
    const response: ResponseInit = {};
    const { result, backend } = resolve(`${ID}-farol`, response);
    const value = firstValueFrom(result as Observable<unknown>);
    backend.expectOne(`/api/loja/pecas/${ID}`).flush(null, { status: 404, statusText: 'Not Found' });

    expect(await value).toBeNull();
    expect(response.status).toBe(404);
  });

  it('endereço sem id nem chama a API', () => {
    const response: ResponseInit = {};
    const { result } = resolve('farol-gol', response);

    expect(isObservable(result)).toBe(false);
    expect(result).toBeNull();
    expect(response.status).toBe(404);
  });

  it('título desatualizado redireciona (301 no servidor) para o endereço atual', async () => {
    const response: ResponseInit = {};
    const { result, backend } = resolve(`${ID}-titulo-antigo`, response);
    const value = firstValueFrom(result as Observable<unknown>);
    backend.expectOne(`/api/loja/pecas/${ID}`).flush(part);

    const redirect = (await value) as RedirectCommand;
    expect(redirect).toBeInstanceOf(RedirectCommand);
    expect(redirect.redirectTo.toString()).toBe(partPath(part));
    expect(response.status).toBe(301);
    expect((response.headers as Record<string, string>)['Location']).toBe(partPath(part));
  });

  it('endereço atual devolve a peça', async () => {
    const { result, backend } = resolve(partPath(part).slice('/peca/'.length), {});
    const value = firstValueFrom(result as Observable<unknown>);
    backend.expectOne(`/api/loja/pecas/${ID}`).flush(part);

    expect(await value).toEqual(part);
  });
});
