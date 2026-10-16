import { registerLocaleData } from '@angular/common';
import localePt from '@angular/common/locales/pt';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { LOCALE_ID } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { PlatformTheme } from './platform.api';
import { PlatformThemePage, previewDocument } from './theme-editor';

registerLocaleData(localePt, 'pt-BR');

const theme: PlatformTheme = {
  tenantId: 't1',
  tradeName: 'Peças do João',
  customStorefront: false,
  css: 'h1 { color: red }',
  headerHtml: '<div class="banner">Promoção</div>',
  footerHtml: '',
  updatedAt: null,
  updatedBy: null,
  pages: [],
};

async function settle(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }) {
  await fixture.whenStable();
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

function type(element: HTMLElement, selector: string, value: string) {
  const field = element.querySelector<HTMLInputElement | HTMLTextAreaElement>(selector)!;
  field.value = value;
  field.dispatchEvent(new Event('input'));
}

describe('Tema exclusivo (RF40)', () => {
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), { provide: LOCALE_ID, useValue: 'pt-BR' }],
    });
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

  async function open() {
    const fixture = TestBed.createComponent(PlatformThemePage);
    fixture.componentRef.setInput('id', 't1');
    fixture.detectChanges();
    backend.expectOne('/api/plataforma/lojas/t1/tema').flush(theme);
    await settle(fixture);
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  it('avisa que a loja personalizada está desligada e salva o tema mostrando o que a limpeza removeu', async () => {
    const { fixture, element } = await open();
    expect(element.querySelector('[data-custom-off]')).not.toBeNull();
    expect(element.querySelector<HTMLTextAreaElement>('[data-css]')!.value).toBe('h1 { color: red }');

    type(element, '[data-header]', '<div>Oi<script>x()</script></div>');
    element.querySelector<HTMLButtonElement>('[data-save-theme]')!.click();
    const request = backend.expectOne((r) => r.method === 'PUT' && r.url === '/api/plataforma/lojas/t1/tema');
    expect(request.request.body).toEqual({ css: 'h1 { color: red }', cabecalho: '<div>Oi<script>x()</script></div>', rodape: '' });
    request.flush({ theme: { ...theme, headerHtml: '<div>Oi</div>', updatedAt: '2026-10-16T12:00:00Z', updatedBy: 'sa@plataforma.test' }, removed: ['Cabeçalho: tag <script>'] });
    await settle(fixture);

    expect(element.querySelector('[data-removed]')!.textContent).toContain('Cabeçalho: tag <script>');
    expect(element.querySelector<HTMLTextAreaElement>('[data-header]')!.value).toBe('<div>Oi</div>');
    expect(element.textContent).toContain('por sa@plataforma.test');
  });

  it('a prévia abre num iframe com sandbox vazio', async () => {
    const { fixture, element } = await open();

    element.querySelector<HTMLButtonElement>('[data-preview]')!.click();
    backend
      .expectOne('/api/plataforma/lojas/t1/tema/previa')
      .flush({ css: 'h1 { color: rgba(255, 0, 0, 1) }', headerHtml: '<div class="banner">Promoção</div>', footerHtml: '', removed: [] });
    await settle(fixture);

    const frame = element.querySelector<HTMLIFrameElement>('[data-preview-frame]')!;
    expect(frame.getAttribute('sandbox')).toBe('');
    expect(frame.getAttribute('srcdoc')).toContain('<div class="banner">Promoção</div>');
  });

  it('cria uma página e passa a editá-la', async () => {
    const { fixture, element } = await open();

    type(element, '[data-page-title]', 'Quem somos');
    type(element, '[data-page-slug]', 'quem-somos');
    type(element, '[data-page-html]', '<p>História</p>');
    element.querySelector<HTMLInputElement>('[data-page-published]')!.click();
    element.querySelector<HTMLButtonElement>('[data-save-page]')!.click();
    const request = backend.expectOne((r) => r.method === 'POST' && r.url === '/api/plataforma/lojas/t1/paginas');
    expect(request.request.body).toEqual({ endereco: 'quem-somos', titulo: 'Quem somos', conteudo: '<p>História</p>', publicada: true, ordem: 0 });
    const page = { id: 'p1', slug: 'quem-somos', title: 'Quem somos', html: '<p>História</p>', published: true, position: 0, updatedAt: '2026-10-16T12:00:00Z', updatedBy: 'sa@plataforma.test' };
    request.flush({ theme: { ...theme, pages: [page] }, removed: [] });
    await settle(fixture);

    expect(element.querySelector('tbody')!.textContent).toContain('/p/quem-somos');
    expect(element.querySelector('[data-save-page]')!.textContent).toContain('Salvar página');
  });

  it('documento da prévia nunca leva CSS com "<"', () => {
    const html = previewDocument({ css: 'h1{} </style><script>x()</script>', headerHtml: '', footerHtml: '', removed: [] });

    expect(html).not.toContain('<script>');
  });
});
