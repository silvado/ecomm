import { registerLocaleData } from '@angular/common';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import localePt from '@angular/common/locales/pt';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { FEEDBACK } from '../../core/feedback';
import { PartFormPage } from './part-form';
import { PartSuggestion } from './parts.api';

registerLocaleData(localePt, 'pt-BR');

describe('PartFormPage — preencher com IA (RF30)', () => {
  const suggestion: PartSuggestion = {
    title: 'Farol dianteiro esquerdo Gol G5',
    description: 'Farol do lado do motorista, lente com riscos leves.',
    condition: 'used',
    oemCodes: ['5U0 941 015'],
    compatibilities: [
      { vehicleVersionId: 'v10', brand: 'Volkswagen', model: 'Gol', engine: '1.0 8V Flex (G5)', versionYearFrom: 2009, versionYearTo: 2012, yearFrom: null, yearTo: null },
      { vehicleVersionId: 'v16', brand: 'Volkswagen', model: 'Gol', engine: '1.6 8V Flex (G5)', versionYearFrom: 2009, versionYearTo: 2012, yearFrom: 2011, yearTo: 2012 },
    ],
    unmatchedVehicles: ['Fiat Tempra 2.0 1995–1998'],
  };

  function setup() {
    TestBed.configureTestingModule({
      imports: [PartFormPage],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(PartFormPage);
    fixture.detectChanges();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    return { fixture, el: fixture.nativeElement as HTMLElement, backend: TestBed.inject(HttpTestingController), navigate };
  }

  function choose(el: HTMLElement, files: File[]): void {
    const input = el.querySelector<HTMLInputElement>('fieldset.ai-assist input[type=file]')!;
    Object.defineProperty(input, 'files', { value: files, configurable: true });
    input.dispatchEvent(new Event('change'));
  }

  function button(el: HTMLElement, text: string): HTMLButtonElement {
    return Array.from(el.querySelectorAll('button')).find((b) => b.textContent!.includes(text))!;
  }

  it('preenche o formulário, destaca os campos da IA e, ao salvar, envia fotos e veículos mantidos', async () => {
    const { fixture, el, backend, navigate } = setup();
    const photo = new File(['jpeg'], 'farol.jpg', { type: 'image/jpeg' });
    choose(el, [photo]);
    fixture.detectChanges();
    button(el, 'Preencher com IA').click();

    const request = backend.expectOne({ method: 'POST', url: '/api/painel/pecas/sugestao-ia' });
    expect((request.request.body as FormData).getAll('fotos')).toHaveLength(1);
    request.flush(suggestion);
    await fixture.whenStable();
    fixture.detectChanges();

    const title = el.querySelector<HTMLInputElement>('input[formControlName=title]')!;
    expect(title.value).toBe(suggestion.title);
    expect(el.querySelectorAll('label.ai-suggested')).toHaveLength(4);
    expect(el.textContent).toContain('Gol · 1.6 8V Flex (G5) · 2011–2012');
    expect(el.textContent).toContain('Fiat Tempra 2.0 1995–1998');

    // Editar o título conta como revisado; o veículo 1.0 é desmarcado.
    title.value = 'Farol esquerdo Gol G5';
    title.dispatchEvent(new Event('input'));
    el.querySelectorAll<HTMLInputElement>('.ai-suggested-vehicles input[type=checkbox]')[0].click();
    fixture.detectChanges();
    expect(el.querySelectorAll('label.ai-suggested')).toHaveLength(3);

    for (const [name, value] of [['internalCode', 'FAR-9'], ['price', '320']]) {
      const input = el.querySelector<HTMLInputElement>(`input[formControlName=${name}]`)!;
      input.value = value;
      input.dispatchEvent(new Event('input'));
    }
    fixture.detectChanges();
    button(el, 'Cadastrar').click();

    const create = backend.expectOne({ method: 'POST', url: '/api/painel/pecas' });
    expect(create.request.body).toMatchObject({ internalCode: 'FAR-9', title: 'Farol esquerdo Gol G5', price: 320, oemCodes: ['5U0 941 015'] });
    create.flush({ id: 'p1' });
    await vi.waitFor(() => backend.expectOne({ method: 'POST', url: '/api/painel/pecas/p1/fotos' }).flush({}));
    await vi.waitFor(() => {
      const compat = backend.expectOne({ method: 'POST', url: '/api/painel/pecas/p1/compatibilidades' });
      expect(compat.request.body).toEqual({ vehicleVersionId: 'v16', yearFrom: 2011, yearTo: 2012, suggestedByAi: true });
      compat.flush({});
    });
    await vi.waitFor(() => expect(navigate).toHaveBeenCalledWith(['/pecas', 'p1'], undefined));
    backend.verify();
  });

  it('mostra a mensagem do limite do plano e não mexe no formulário', async () => {
    const { fixture, el, backend } = setup();
    choose(el, [new File(['jpeg'], 'a.jpg', { type: 'image/jpeg' })]);
    fixture.detectChanges();
    button(el, 'Preencher com IA').click();

    backend
      .expectOne('/api/painel/pecas/sugestao-ia')
      .flush({ title: 'O plano Essencial permite até 50 cadastros com IA por mês.' }, { status: 409, statusText: 'Conflict' });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(el.querySelector('fieldset.ai-assist [role=alert]')!.textContent).toContain('50 cadastros com IA');
    expect(el.querySelector<HTMLInputElement>('input[formControlName=title]')!.value).toBe('');
    expect(el.querySelectorAll('label.ai-suggested')).toHaveLength(0);
  });

  it('código em branco: cadastra assim mesmo (o sistema gera) e o aviso mostra o código gerado', async () => {
    const { fixture, el, backend } = setup();
    for (const [name, value] of [['title', 'Lanterna traseira'], ['price', '120']]) {
      const input = el.querySelector<HTMLInputElement>(`input[formControlName=${name}]`)!;
      input.value = value;
      input.dispatchEvent(new Event('input'));
    }
    fixture.detectChanges();
    expect(el.querySelector<HTMLInputElement>('input[formControlName=internalCode]')!.required).toBe(false);
    button(el, 'Cadastrar').click();

    const create = backend.expectOne({ method: 'POST', url: '/api/painel/pecas' });
    expect(create.request.body).toMatchObject({ internalCode: '', title: 'Lanterna traseira' });
    const success = create.request.context.get(FEEDBACK) as { success: (body: unknown) => string };
    expect(success.success({ internalCode: 'P-000007' })).toBe('Peça P-000007 cadastrada.');
    create.flush({ id: 'p2', internalCode: 'P-000007' });
  });

  it('recusa mais de 10 fotos antes de enviar', () => {
    const { fixture, el, backend } = setup();
    choose(el, Array.from({ length: 11 }, (_, i) => new File(['x'], `f${i}.jpg`, { type: 'image/jpeg' })));
    fixture.detectChanges();

    expect(el.textContent).toContain('Escolha no máximo 10 fotos.');
    expect(button(el, 'Preencher com IA').disabled).toBe(true);
    backend.verify();
  });
});
