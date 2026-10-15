import { REQUEST_CONTEXT } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Title } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { App } from './app';
import { routes } from './app.routes';
import { LegalPage } from './legal';
import { PublicStore, StoreRequestContext } from './store';

const store: PublicStore = {
  slug: 'pecas-do-joao',
  name: 'Peças do João',
  theme: { primaryColor: '#B42318', onPrimaryColor: '#FFFFFF', backgroundColor: '#FFFFFF', textColor: '#101828' },
  texts: { about: '', returnPolicy: '', footer: '' },
  logoUrl: null,
};

const context: StoreRequestContext = {
  store: { kind: 'open', store },
  origin: 'https://loja.test',
  api: { url: 'http://api:8080', host: 'loja.test', proto: 'https', clientIp: '203.0.113.1' },
};

describe('Privacidade e termos (RNF04)', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({ providers: [provideRouter(routes), { provide: REQUEST_CONTEXT, useValue: context }] }),
  );

  it('o rodapé sempre leva aos termos e à política, mesmo sem texto de trocas', () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    const links = [...(fixture.nativeElement as HTMLElement).querySelectorAll('footer a')].map((a) => a.getAttribute('href'));

    expect(links).toEqual(['/termos', '/privacidade']);
  });

  it('mostra o texto da página pedida e o título certo', () => {
    const fixture = TestBed.createComponent(LegalPage);
    fixture.componentRef.setInput('kind', 'terms');
    fixture.componentRef.setInput('texts', { privacyPolicy: 'Política da loja.', terms: 'Termos da loja.' });
    fixture.detectChanges();
    const page = fixture.nativeElement as HTMLElement;

    expect(page.querySelector('h1')!.textContent).toBe('Termos de uso');
    expect(page.querySelector('[data-legal-text]')!.textContent).toBe('Termos da loja.');
    expect(TestBed.inject(Title).getTitle()).toBe('Termos de uso — Peças do João');
  });

  it('sem a API, avisa em vez de mostrar página vazia', () => {
    const fixture = TestBed.createComponent(LegalPage);
    fixture.componentRef.setInput('kind', 'privacy');
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Não foi possível carregar o texto');
  });
});
