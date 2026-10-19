import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { feedback, FeedbackHost, feedbackInterceptor, FeedbackService, OVERLAY_MIN_MS, silent } from './feedback';

describe('Retorno das ações (overlay e avisos)', () => {
  let http: HttpClient;
  let backend: HttpTestingController;
  let service: FeedbackService;

  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({ providers: [provideHttpClient(withInterceptors([feedbackInterceptor])), provideHttpClientTesting()] });
    http = TestBed.inject(HttpClient);
    backend = TestBed.inject(HttpTestingController);
    service = TestBed.inject(FeedbackService);
  });

  afterEach(() => {
    backend.verify();
    vi.useRealTimers();
  });

  const texts = () => service.toasts().map((t) => `${t.kind}: ${t.text}`);

  it('ação que grava: overlay no clique, some ao terminar, e aviso de sucesso', async () => {
    const call = firstValueFrom(http.put('/api/painel/loja', {}, { context: feedback('Dados salvos.') }));
    expect(service.busy()).toBe(true);
    vi.advanceTimersByTime(OVERLAY_MIN_MS);

    backend.expectOne('/api/painel/loja').flush({});
    await call;
    expect(service.busy()).toBe(false);
    expect(texts()).toEqual(['success: Dados salvos.']);

    vi.advanceTimersByTime(4000); // o aviso de sucesso some sozinho
    expect(texts()).toEqual([]);
  });

  it('ação rápida mantém o overlay o tempo mínimo; sem texto, aviso padrão pelo tipo de ação', async () => {
    const save = firstValueFrom(http.post('/api/painel/x', {}));
    backend.expectOne('/api/painel/x').flush({});
    await save;
    expect(service.busy()).toBe(true);

    // Outra ação antes de o overlay sumir: continua um só, sem piscar.
    const remove = firstValueFrom(http.delete('/api/painel/x/1'));
    vi.advanceTimersByTime(OVERLAY_MIN_MS);
    expect(service.busy()).toBe(true);
    backend.expectOne('/api/painel/x/1').flush(null);
    await remove;
    expect(service.busy()).toBe(false);
    expect(texts()).toEqual(['success: Feito.', 'success: Removido.']);
  });

  it('erro mostra a mensagem da API; sem conexão, um texto próprio', async () => {
    const fail = firstValueFrom(http.post('/api/painel/pecas', {}));
    backend.expectOne('/api/painel/pecas').flush({ title: 'Código interno já usado por outra peça.' }, { status: 409, statusText: 'Conflict' });
    await expect(fail).rejects.toBeTruthy();

    const offline = firstValueFrom(http.post('/api/painel/pecas', {}));
    backend.expectOne('/api/painel/pecas').error(new ProgressEvent('error'), { status: 0 });
    await expect(offline).rejects.toBeTruthy();

    expect(texts()).toEqual(['error: Código interno já usado por outra peça.', 'error: Sem conexão com o servidor. Confira a internet e tente de novo.']);
    vi.advanceTimersByTime(OVERLAY_MIN_MS);
    expect(service.busy()).toBe(false);
  });

  it('sem aviso de sucesso quando a tela mostra o resultado; sem overlay em ação com progresso próprio', async () => {
    const preview = firstValueFrom(http.post('/api/painel/importacoes', {}, { context: feedback(null) }));
    expect(service.busy()).toBe(true);
    backend.expectOne('/api/painel/importacoes').flush({});
    await preview;
    vi.advanceTimersByTime(OVERLAY_MIN_MS);

    const photo = firstValueFrom(http.post('/api/painel/pecas/1/fotos', {}, { context: feedback(null, { overlay: false }) }));
    expect(service.busy()).toBe(false);
    backend.expectOne('/api/painel/pecas/1/fotos').flush({});
    await photo;
    expect(texts()).toEqual([]);
  });

  it('leituras, sessão e ações silenciosas ficam de fora', async () => {
    const calls = [
      firstValueFrom(http.get('/api/painel/pecas')),
      firstValueFrom(http.post('/api/auth/login', {})),
      firstValueFrom(http.post('/api/plataforma/auth/entrar', {})),
      firstValueFrom(http.post('/api/plataforma/lojas/1/tema/previa', {}, { context: silent() })),
    ];
    expect(service.busy()).toBe(false);
    backend.expectOne('/api/painel/pecas').flush([]);
    backend.expectOne('/api/auth/login').flush({}, { status: 401, statusText: 'Unauthorized' });
    backend.expectOne('/api/plataforma/auth/entrar').flush({});
    backend.expectOne('/api/plataforma/lojas/1/tema/previa').flush({}, { status: 400, statusText: 'Bad Request' });
    await Promise.allSettled(calls);
    expect(texts()).toEqual([]);
  });

  it('mostra no máximo 4 avisos; o de erro fica mais tempo e pode ser fechado', async () => {
    for (let i = 1; i <= 5; i++) service.success(`Aviso ${i}`);
    expect(texts()).toEqual(['success: Aviso 2', 'success: Aviso 3', 'success: Aviso 4', 'success: Aviso 5']);
    vi.advanceTimersByTime(4000);
    service.error('Falhou.');
    vi.advanceTimersByTime(4000);
    expect(texts()).toEqual(['error: Falhou.']);

    const fixture = TestBed.createComponent(FeedbackHost);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('[data-toast="error"]')!.getAttribute('role')).toBe('alert');
    element.querySelector<HTMLButtonElement>('.toast-close')!.click();
    fixture.detectChanges();
    expect(element.querySelector('.toast')).toBeNull();
  });
});
