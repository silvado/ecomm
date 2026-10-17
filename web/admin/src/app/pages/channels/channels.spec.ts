import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter, Router } from '@angular/router';
import { ChannelReturnPage } from './channel-return';
import { ChannelConnection, ChannelsOverview } from './channels.api';
import { ChannelsPage, ExternalNavigation } from './channels';

describe('ChannelsPage (RF21)', () => {
  const connection: ChannelConnection = {
    id: 'c1',
    channel: 'mercado-livre',
    externalAccountId: '1234567',
    accountName: 'AUTOPECAS_PILOTO',
    status: 'active',
    statusReason: null,
    createdAt: '2026-10-16T12:00:00Z',
    updatedAt: null,
  };

  function setup(overview: ChannelsOverview) {
    const go = vi.fn();
    TestBed.configureTestingModule({
      imports: [ChannelsPage],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting(), { provide: ExternalNavigation, useValue: { go } }],
    });
    const fixture = TestBed.createComponent(ChannelsPage);
    fixture.detectChanges();
    const backend = TestBed.inject(HttpTestingController);
    backend.expectOne('/api/painel/canais').flush(overview);
    return { fixture, el: fixture.nativeElement as HTMLElement, backend, go };
  }

  async function settle(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }) {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  it('sem conta, conectar leva à página de autorização do Mercado Livre', async () => {
    const { fixture, el, backend, go } = setup({ mercadoLivreAvailable: true, mercadoLivreInPlan: true, connections: [] });
    await settle(fixture);
    expect(el.textContent).toContain('Nenhuma conta conectada.');

    el.querySelector<HTMLButtonElement>('[data-connect]')!.click();
    backend.expectOne({ method: 'POST', url: '/api/painel/canais/mercado-livre/conectar' }).flush({ authorizationUrl: 'https://auth.ml.test/authorization?state=x' });
    await vi.waitFor(() => expect(go).toHaveBeenCalledWith('https://auth.ml.test/authorization?state=x'));
  });

  it('conta conectada aparece com o apelido e sem o botão de conectar outra', async () => {
    const { fixture, el } = setup({ mercadoLivreAvailable: true, mercadoLivreInPlan: true, connections: [connection, { ...connection, id: 'old', status: 'disabled' }] });
    await settle(fixture);

    expect(el.querySelectorAll('[data-connection]')).toHaveLength(1);
    expect(el.querySelector('[data-connection="c1"]')!.textContent).toContain('AUTOPECAS_PILOTO');
    expect(el.querySelector('[data-connection="c1"]')!.textContent).toContain('Conectada');
    expect(el.querySelector('[data-connect]')).toBeNull();
  });

  it('pedindo nova autorização mostra o motivo e o botão de autorizar de novo', async () => {
    const reason = 'O Mercado Livre recusou o acesso (autorização vencida, revogada ou senha alterada). Conecte a conta de novo.';
    const { fixture, el } = setup({ mercadoLivreAvailable: true, mercadoLivreInPlan: true, connections: [{ ...connection, status: 'reauthRequired', statusReason: reason }] });
    await settle(fixture);

    expect(el.querySelector('[role=alert]')!.textContent).toContain(reason);
    expect(Array.from(el.querySelectorAll('button')).some((b) => b.textContent!.includes('Autorizar de novo'))).toBe(true);
  });

  it('desconectar pede confirmação e recarrega', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    const { fixture, el, backend } = setup({ mercadoLivreAvailable: true, mercadoLivreInPlan: true, connections: [connection] });
    await settle(fixture);

    Array.from(el.querySelectorAll<HTMLButtonElement>('button')).find((b) => b.textContent!.includes('Desconectar'))!.click();
    backend.expectOne({ method: 'DELETE', url: '/api/painel/canais/c1' }).flush(null);
    await vi.waitFor(() => backend.expectOne('/api/painel/canais').flush({ mercadoLivreAvailable: true, mercadoLivreInPlan: true, connections: [] }));
    await settle(fixture);
    expect(el.textContent).toContain('Nenhuma conta conectada.');
  });

  it('instalação sem o app ou plano sem o canal explica o motivo', async () => {
    const { fixture, el } = setup({ mercadoLivreAvailable: false, mercadoLivreInPlan: true, connections: [] });
    await settle(fixture);
    expect(el.textContent).toContain('ainda não está disponível nesta instalação');
    expect(el.querySelector('[data-connect]')).toBeNull();
  });
});

describe('ChannelReturnPage (RF21)', () => {
  function setup(query: Record<string, string>) {
    TestBed.configureTestingModule({
      imports: [ChannelReturnPage],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(query) } } },
      ],
    });
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const fixture = TestBed.createComponent(ChannelReturnPage);
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement, backend: TestBed.inject(HttpTestingController), navigate };
  }

  it('repassa código e state e volta para Canais de venda sem o código na barra', async () => {
    const { backend, navigate } = setup({ code: 'TG-abc', state: 'st' });

    const request = backend.expectOne({ method: 'POST', url: '/api/painel/canais/mercado-livre/retorno' });
    expect(request.request.body).toEqual({ code: 'TG-abc', state: 'st', error: null });
    request.flush({});
    await vi.waitFor(() => expect(navigate).toHaveBeenCalledWith(['/canais'], { replaceUrl: true }));
  });

  it('mostra o motivo quando a autorização foi negada ou venceu', async () => {
    const { fixture, el, backend } = setup({ error: 'access_denied', state: 'st' });

    backend
      .expectOne('/api/painel/canais/mercado-livre/retorno')
      .flush({ title: 'A autorização foi negada no Mercado Livre. Nada foi conectado.' }, { status: 400, statusText: 'Bad Request' });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(el.querySelector('[role=alert]')!.textContent).toContain('negada no Mercado Livre');
  });
});
