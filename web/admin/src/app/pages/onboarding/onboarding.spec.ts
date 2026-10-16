import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { OnboardingCard, OnboardingPage, OnboardingView } from './onboarding';

const pending: OnboardingView = {
  published: false,
  canPublish: false,
  steps: [
    { step: 'company', required: true, done: true, skipped: false, detail: null },
    { step: 'payment', required: true, done: true, skipped: false, detail: 'Pagamento simulado (ambiente de teste).' },
    { step: 'fiscal', required: false, done: false, skipped: false, detail: null },
    { step: 'shipping', required: true, done: false, skipped: false, detail: null },
    { step: 'mercadoLivre', required: false, done: false, skipped: false, detail: null },
  ],
};

async function settle(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }) {
  await fixture.whenStable();
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

describe('Primeiros passos (RF05)', () => {
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

  async function open(view: OnboardingView) {
    const fixture = TestBed.createComponent(OnboardingPage);
    fixture.detectChanges();
    backend.expectOne('/api/painel/primeiros-passos').flush(view);
    await settle(fixture);
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  it('mostra o progresso, o que falta e trava o botão de publicar', async () => {
    const { element } = await open(pending);

    expect(element.querySelector('[data-progress]')!.textContent).toContain('2 de 5');
    expect(element.querySelector('[data-step="payment"]')!.textContent).toContain('Pagamento simulado');
    expect(element.querySelector('[data-step="shipping"]')!.textContent).toContain('Obrigatório');
    expect(element.querySelector('[data-skip="shipping"]')).toBeNull();
    expect(element.querySelector<HTMLButtonElement>('[data-publish]')!.disabled).toBe(true);
    expect(element.textContent).toContain('Faltam: Frete.');
  });

  it('pula um passo opcional', async () => {
    const { fixture, element } = await open(pending);

    element.querySelector<HTMLButtonElement>('[data-skip="mercadoLivre"]')!.click();
    const request = backend.expectOne('/api/painel/primeiros-passos/mercadoLivre/pular');
    expect(request.request.method).toBe('PUT');
    request.flush({ ...pending, steps: pending.steps.map((s) => (s.step === 'mercadoLivre' ? { ...s, skipped: true } : s)) });
    await settle(fixture);

    expect(element.querySelector('[data-step="mercadoLivre"]')!.textContent).toContain('Pulado');
    expect(element.querySelector('[data-skip="mercadoLivre"]')!.textContent).toContain('Mostrar de novo');
  });

  it('publica quando os obrigatórios estão prontos', async () => {
    const ready: OnboardingView = { ...pending, canPublish: true, steps: pending.steps.map((s) => (s.step === 'shipping' ? { ...s, done: true } : s)) };
    const { fixture, element } = await open(ready);

    element.querySelector<HTMLButtonElement>('[data-publish]')!.click();
    backend.expectOne((r) => r.method === 'POST' && r.url === '/api/painel/primeiros-passos/publicar').flush({ ...ready, published: true });
    await settle(fixture);

    expect(element.querySelector('[data-published]')).not.toBeNull();
    expect(element.querySelector('[data-publish]')).toBeNull();
  });

  it('o cartão do Início some depois de publicada', async () => {
    const fixture = TestBed.createComponent(OnboardingCard);
    fixture.detectChanges();
    backend.expectOne('/api/painel/primeiros-passos').flush(pending);
    await settle(fixture);
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-onboarding-card]')!.textContent).toContain('2 de 5');

    const published = TestBed.createComponent(OnboardingCard);
    published.detectChanges();
    backend.expectOne('/api/painel/primeiros-passos').flush({ ...pending, published: true });
    await settle(published);
    expect((published.nativeElement as HTMLElement).querySelector('[data-onboarding-card]')).toBeNull();
  });
});
