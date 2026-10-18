import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthStore } from '../../core/auth';
import { BuyerQuestion } from './questions.api';
import { QuestionsPage } from './questions';

describe('QuestionsPage (RF31)', () => {
  const question: BuyerQuestion = {
    id: 'q1',
    channel: 'mercado-livre',
    externalItemId: 'MLB123',
    partId: 'p1',
    partTitle: 'Farol dianteiro esquerdo Gol',
    text: 'Serve no Gol 2010?',
    askedAt: '2026-10-18T12:00:00Z',
    status: 'pending',
    draftText: 'Olá! [confirmar o ano do veículo]',
    kind: 'complex',
    draftNote: 'Pergunta sobre compatibilidade: confira os veículos da peça antes de enviar.',
    answerText: null,
    answeredVia: null,
    answeredAt: null,
    lastError: null,
  };

  function setup(list: BuyerQuestion[], owner = true, autoAnswer = false) {
    TestBed.configureTestingModule({
      imports: [QuestionsPage],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting(), { provide: AuthStore, useValue: { can: () => owner } }],
    });
    const fixture = TestBed.createComponent(QuestionsPage);
    fixture.detectChanges();
    const backend = TestBed.inject(HttpTestingController);
    backend.expectOne('/api/painel/perguntas/configuracao').flush({ autoAnswer });
    return { fixture, el: fixture.nativeElement as HTMLElement, backend, list };
  }

  async function settle(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }) {
    for (let i = 0; i < 3; i++) {
      await fixture.whenStable();
      fixture.detectChanges();
    }
  }

  async function loaded(list: BuyerQuestion[], owner = true, autoAnswer = false) {
    const ctx = setup(list, owner, autoAnswer);
    await vi.waitFor(() => ctx.backend.expectOne('/api/painel/perguntas').flush(list));
    await settle(ctx.fixture);
    return ctx;
  }

  it('mostra a pergunta com o rascunho da IA e a nota de revisão', async () => {
    const { el } = await loaded([question]);
    const card = el.querySelector('[data-question="q1"]')!;
    expect(card.textContent).toContain('Serve no Gol 2010?');
    expect(card.textContent).toContain('Revisar');
    expect(card.querySelector('[data-note]')!.textContent).toContain('compatibilidade');
    expect(card.querySelector<HTMLTextAreaElement>('[data-answer]')!.value).toBe('Olá! [confirmar o ano do veículo]');
  });

  it('enviar manda o texto editado e tira a pergunta da lista', async () => {
    const { fixture, el, backend } = await loaded([question]);
    const textarea = el.querySelector<HTMLTextAreaElement>('[data-answer]')!;
    textarea.value = 'Serve sim, do 2009 ao 2012.';
    textarea.dispatchEvent(new Event('input'));
    await settle(fixture);

    el.querySelector<HTMLButtonElement>('[data-send]')!.click();
    const request = backend.expectOne({ method: 'POST', url: '/api/painel/perguntas/q1/responder' });
    expect(request.request.body).toEqual({ texto: 'Serve sim, do 2009 ao 2012.' });
    request.flush({ ...question, status: 'answered', answerText: 'Serve sim, do 2009 ao 2012.', answeredVia: 'panel' });
    await settle(fixture);
    expect(el.querySelector('[data-question="q1"]')).toBeNull();
    expect(el.textContent).toContain('Nenhuma pergunta esperando resposta.');
  });

  it('recusa do Mercado Livre aparece como erro', async () => {
    const { fixture, el, backend } = await loaded([question]);
    el.querySelector<HTMLButtonElement>('[data-send]')!.click();
    backend
      .expectOne({ method: 'POST', url: '/api/painel/perguntas/q1/responder' })
      .flush({ title: 'O Mercado Livre recusou a resposta: The question is not unanswered.' }, { status: 422, statusText: 'Unprocessable' });
    await vi.waitFor(() => backend.expectOne('/api/painel/perguntas').flush([question]));
    await settle(fixture);
    expect(el.querySelector('[role=alert]')!.textContent).toContain('O Mercado Livre recusou a resposta');
  });

  it('o Dono liga a resposta automática com confirmação', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    const { fixture, el, backend } = await loaded([]);
    const box = el.querySelector<HTMLInputElement>('[data-auto]')!;
    expect(box.checked).toBe(false);
    box.click();
    const request = backend.expectOne({ method: 'PUT', url: '/api/painel/perguntas/configuracao' });
    expect(request.request.body).toEqual({ automatica: true });
    request.flush({ autoAnswer: true });
    await settle(fixture);
    expect(box.checked).toBe(true);
  });

  it('o Operador não vê a opção de resposta automática', async () => {
    const { el } = await loaded([], false, true);
    expect(el.querySelector('[data-auto]')).toBeNull();
    expect(el.textContent).toContain('A resposta automática das perguntas simples está ligada.');
  });
});
