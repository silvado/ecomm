import { makeStateKey, REQUEST_CONTEXT, RESPONSE_INIT, TransferState } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';
import { routes } from './app.routes';
import { PublicStore, StoreRequestContext, StoreState } from './store';

const store: PublicStore = {
  slug: 'pecas-do-joao',
  name: 'Peças do João',
  theme: { primaryColor: '#B42318', onPrimaryColor: '#FFFFFF', backgroundColor: '#FFFFFF', textColor: '#101828' },
  texts: { about: 'Desde 1998.', returnPolicy: '7 dias.', footer: 'Rua A, 1' },
};

function render(state: StoreState, response: ResponseInit = {}) {
  TestBed.configureTestingModule({
    imports: [App],
    providers: [
      provideRouter(routes),
      { provide: REQUEST_CONTEXT, useValue: { store: state } satisfies StoreRequestContext },
      { provide: RESPONSE_INIT, useValue: response },
    ],
  });
  const fixture = TestBed.createComponent(App);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

describe('App (loja)', () => {
  it('aplica o tema da loja como variáveis CSS e mostra nome e rodapé', () => {
    const page = render({ kind: 'open', store });

    const root = page.querySelector<HTMLElement>('.store')!;
    expect(root.style.getPropertyValue('--color-primary')).toBe('#B42318');
    expect(root.style.getPropertyValue('--color-on-primary')).toBe('#FFFFFF');
    expect(page.querySelector('header')?.textContent).toContain('Peças do João');
    expect(page.querySelector('footer')?.textContent).toContain('Rua A, 1');
  });

  it('loja suspensa responde 503 com aviso', () => {
    const response: ResponseInit = {};
    const page = render({ kind: 'unavailable' }, response);

    expect(response.status).toBe(503);
    expect(page.textContent).toContain('Loja indisponível no momento');
  });

  it('host desconhecido responde 404', () => {
    const response: ResponseInit = {};
    const page = render({ kind: 'not-found' }, response);

    expect(response.status).toBe(404);
    expect(page.textContent).toContain('Loja não encontrada');
  });

  it('passa a loja para a hidratação pelo TransferState', () => {
    render({ kind: 'open', store });

    expect(TestBed.inject(TransferState).get(makeStateKey<StoreState>('store'), { kind: 'error' })).toEqual({ kind: 'open', store });
  });
});
