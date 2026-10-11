import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { PartImportView } from './part-import.api';
import { PartImportPage } from './part-import';

describe('PartImportPage', () => {
  const view = (extra: Partial<PartImportView> = {}): PartImportView => ({
    id: 'i1',
    fileName: 'pecas.xlsx',
    status: 'validated',
    createdAt: '2026-10-11T12:00:00Z',
    startedAt: null,
    finishedAt: null,
    totalRows: 3,
    validRows: 2,
    errorCount: 1,
    existingCodesAtPreview: 1,
    processedRows: 0,
    importedCount: 0,
    skippedCount: 0,
    errors: [{ line: 4, message: 'Preço inválido: use, por exemplo, 350,00.' }],
    skipped: [],
    ...extra,
  });

  async function setup() {
    TestBed.configureTestingModule({ imports: [PartImportPage], providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    const fixture = TestBed.createComponent(PartImportPage);
    fixture.detectChanges();
    const backend = TestBed.inject(HttpTestingController);
    backend.expectOne({ method: 'GET', url: '/api/painel/pecas/importacoes' }).flush([]);
    await fixture.whenStable();
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const settle = async () => {
      await fixture.whenStable();
      fixture.detectChanges();
    };
    return { fixture, backend, element, settle };
  }

  function choose(element: HTMLElement, file: File): void {
    const input = element.querySelector<HTMLInputElement>('input[type=file]')!;
    Object.defineProperty(input, 'files', { value: [file], configurable: true });
    input.dispatchEvent(new Event('change'));
  }

  it('envia o arquivo, mostra a prévia com erros e acompanha a importação até concluir', async () => {
    const { backend, element, settle } = await setup();
    const file = new File(['codigo;titulo'], 'pecas.xlsx');

    choose(element, file);
    const preview = backend.expectOne((r) => r.method === 'POST' && r.url === '/api/painel/pecas/importacoes');
    expect(preview.request.params.get('arquivo')).toBe('pecas.xlsx');
    expect(preview.request.body).toBe(file);
    preview.flush(view());
    await settle();

    expect(element.querySelector('[data-valid]')!.textContent).toContain('2 válidas');
    expect(element.querySelector('[data-import]')!.textContent).toContain('1 com código já cadastrado');
    expect(element.querySelector('[data-error-list]')!.textContent).toContain('Preço inválido');

    element.querySelector<HTMLButtonElement>('[data-confirm]')!.click();
    backend.expectOne({ method: 'POST', url: '/api/painel/pecas/importacoes/i1/confirmar' })
      .flush(view({ status: 'processing', processedRows: 1, importedCount: 1 }));
    await settle();
    expect(element.querySelector('[data-progress]')!.textContent).toContain('1 de 2 processadas');

    await new Promise((resolve) => setTimeout(resolve, 1100)); // próxima consulta do progresso
    backend.expectOne({ method: 'GET', url: '/api/painel/pecas/importacoes/i1' })
      .flush(view({ status: 'completed', processedRows: 2, importedCount: 1, skippedCount: 1, skipped: [{ line: 3, internalCode: 'K-3' }] }));
    await settle();
    backend.expectOne({ method: 'GET', url: '/api/painel/pecas/importacoes' }).flush([]);
    await settle();

    expect(element.querySelector('[role=status]')!.textContent).toContain('Pronto');
    expect(element.querySelector('[data-import]')!.textContent).toContain('K-3 (linha 3)');
    backend.verify();
  });

  it('arquivo grande demais é recusado sem enviar', async () => {
    const { backend, element, settle } = await setup();

    choose(element, new File([new Uint8Array(5 * 1024 * 1024 + 1)], 'grande.csv'));
    await settle();

    expect(element.querySelector('[role=alert]')!.textContent).toContain('no máximo 5 MB');
    backend.verify();
  });

  it('planilha recusada pela API mostra o motivo', async () => {
    const { backend, element, settle } = await setup();

    choose(element, new File(['x'], 'pecas.csv'));
    backend.expectOne('/api/painel/pecas/importacoes?arquivo=pecas.csv')
      .flush({ title: 'Faltam colunas obrigatórias no cabeçalho (linha 1): preco. Use o modelo.' }, { status: 400, statusText: 'Bad Request' });
    await settle();

    expect(element.querySelector('[role=alert]')!.textContent).toContain('Faltam colunas obrigatórias');
    expect(element.querySelector('[data-import]')).toBeNull();
    backend.verify();
  });
});
