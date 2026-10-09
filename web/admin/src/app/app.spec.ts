import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { App } from './app';
import { routes } from './app.routes';

describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter(routes), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
  });

  it('sem sessão, qualquer rota leva ao login', async () => {
    const fixture = TestBed.createComponent(App);
    await TestBed.inject(Router).navigateByUrl('/usuarios');
    await fixture.whenStable();

    expect(TestBed.inject(Router).url).toBe('/entrar');
    expect((fixture.nativeElement as HTMLElement).querySelector('h1')?.textContent).toContain('Entrar no painel');
  });
});
