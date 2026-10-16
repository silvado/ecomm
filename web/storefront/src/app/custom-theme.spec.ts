import { DOCUMENT } from '@angular/common';
import { REQUEST_CONTEXT } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Title } from '@angular/platform-browser';
import { provideRouter, Router } from '@angular/router';
import { App } from './app';
import { routes } from './app.routes';
import { CustomPage, CustomThemeStyle, isThemedPath } from './custom-theme';
import { PublicStore, StoreRequestContext } from './store';

const base: PublicStore = {
  slug: 'pecas-do-joao',
  name: 'Peças do João',
  theme: { primaryColor: '#B42318', onPrimaryColor: '#FFFFFF', backgroundColor: '#FFFFFF', textColor: '#101828' },
  texts: { about: '', returnPolicy: '', footer: 'Rua A, 1' },
  logoUrl: null,
};

const custom: PublicStore = {
  ...base,
  custom: {
    css: '.custom-theme {\nh1 { color: rgba(176, 0, 32, 1) }\n}',
    headerHtml: '<div class="banner">Promoção de faróis</div>',
    footerHtml: '<p class="assinatura">Desde 1990</p>',
    pages: [{ slug: 'quem-somos', title: 'Quem somos' }],
  },
};

function setup(store: PublicStore) {
  TestBed.configureTestingModule({
    providers: [
      provideRouter(routes),
      {
        provide: REQUEST_CONTEXT,
        useValue: {
          store: { kind: 'open', store },
          origin: 'https://loja.test',
          api: { url: 'http://api:8080', host: 'loja.test', proto: 'https', clientIp: '203.0.113.1' },
        } satisfies StoreRequestContext,
      },
    ],
  });
}

describe('Loja personalizada: tema exclusivo (RF40)', () => {
  afterEach(() => TestBed.inject(DOCUMENT).getElementById('custom-theme')?.remove());

  it('aplica o CSS no <head>, o cabeçalho, o rodapé e os links das páginas', () => {
    setup(custom);
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    const page = fixture.nativeElement as HTMLElement;

    expect(TestBed.inject(DOCUMENT).getElementById('custom-theme')!.textContent).toContain('.custom-theme');
    expect(page.querySelector('[data-custom-header]')!.textContent).toBe('Promoção de faróis');
    expect(page.querySelector('[data-custom-footer]')!.textContent).toBe('Desde 1990');
    expect(page.querySelector('footer')!.textContent).not.toContain('Rua A, 1');
    const links = [...page.querySelectorAll('footer a')].map((a) => a.getAttribute('href'));
    expect(links).toEqual(['/p/quem-somos', '/termos', '/privacidade']);
    expect(page.querySelector('header')!.classList).toContain('custom-theme');
    expect(page.querySelector('main')!.classList).toContain('custom-theme');
  });

  it('no carrinho, o miolo da página fica sem o tema exclusivo', async () => {
    setup(custom);
    const fixture = TestBed.createComponent(App);
    await TestBed.inject(Router).navigateByUrl('/carrinho');
    fixture.detectChanges();
    const page = fixture.nativeElement as HTMLElement;

    expect(page.querySelector('main')!.classList).not.toContain('custom-theme');
    expect(page.querySelector('footer')!.classList).toContain('custom-theme');
  });

  it('sem loja personalizada, nada muda', () => {
    setup(base);
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    const page = fixture.nativeElement as HTMLElement;

    expect(TestBed.inject(DOCUMENT).getElementById('custom-theme')).toBeNull();
    expect(page.querySelector('[data-custom-header]')).toBeNull();
    expect(page.querySelector('footer')!.textContent).toContain('Rua A, 1');
    expect(page.querySelector('header')!.classList).not.toContain('custom-theme');
  });

  it('CSS com "<" nunca vai para o <style>', () => {
    setup(base);
    TestBed.inject(CustomThemeStyle).apply('h1{} </style><script>alert(1)</script>');

    expect(TestBed.inject(DOCUMENT).getElementById('custom-theme')).toBeNull();
  });

  it('só carrinho, pedido e Meus pedidos ficam fora do tema', () => {
    expect(['/', '/busca?q=farol', '/peca/abc', '/p/quem-somos'].every(isThemedPath)).toBe(true);
    expect(['/carrinho', '/pedido#token', '/meus-pedidos?x=1'].some(isThemedPath)).toBe(false);
  });

  it('página exclusiva mostra título e conteúdo; inexistente avisa', () => {
    setup(custom);
    const fixture = TestBed.createComponent(CustomPage);
    fixture.componentRef.setInput('page', { slug: 'quem-somos', title: 'Quem somos', html: '<h2>Nossa história</h2>' });
    fixture.detectChanges();
    const page = fixture.nativeElement as HTMLElement;

    expect(page.querySelector('h1')!.textContent).toBe('Quem somos');
    expect(page.querySelector('h2')!.textContent).toBe('Nossa história');
    expect(TestBed.inject(Title).getTitle()).toBe('Quem somos — Peças do João');

    const missing = TestBed.createComponent(CustomPage);
    missing.detectChanges();
    expect((missing.nativeElement as HTMLElement).textContent).toContain('Página não encontrada');
  });
});
