import { registerLocaleData } from '@angular/common';
import localePt from '@angular/common/locales/pt';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { LOCALE_ID } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ExportSection } from './export-section';
import { DataExport } from './privacy.api';

registerLocaleData(localePt, 'pt-BR');

const ready: DataExport = {
  id: 'e1',
  status: 'ready',
  requestedByEmail: 'dono@loja.test',
  requestedAt: '2026-10-15T12:00:00Z',
  completedAt: '2026-10-15T12:01:00Z',
  expiresAt: '2026-10-22T12:01:00Z',
  sizeBytes: 3 * 1048576,
  error: null,
  canDownload: true,
};

async function settle(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }) {
  await fixture.whenStable();
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

describe('ExportSection', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), { provide: LOCALE_ID, useValue: 'pt-BR' }] }),
  );

  it('com uma exportação gerando, o botão fica travado', async () => {
    const fixture = TestBed.createComponent(ExportSection);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/painel/privacidade/exportacoes').flush([{ ...ready, status: 'pending', canDownload: false }]);
    await settle(fixture);
    const button = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('[data-request-export]')!;

    expect(button.disabled).toBe(true);
    expect(button.textContent).toContain('Gerando exportação');
    fixture.destroy();
  });

  it('pede a exportação, baixa pelo painel e avisa a tela para recarregar o registro', async () => {
    const fixture = TestBed.createComponent(ExportSection);
    let changed = 0;
    fixture.componentInstance.changed.subscribe(() => changed++);
    fixture.detectChanges();
    const backend = TestBed.inject(HttpTestingController);
    backend.expectOne('/api/painel/privacidade/exportacoes').flush([]);
    await settle(fixture);
    const element = fixture.nativeElement as HTMLElement;

    element.querySelector<HTMLButtonElement>('[data-request-export]')!.click();
    const post = backend.expectOne((r) => r.method === 'POST' && r.url === '/api/painel/privacidade/exportacoes');
    post.flush(ready);
    await settle(fixture);
    backend.expectOne((r) => r.method === 'GET' && r.url === '/api/painel/privacidade/exportacoes').flush([ready]);
    await settle(fixture);
    expect(element.querySelector('tbody')!.textContent).toContain('3,0 MB');
    await settle(fixture);
    expect(element.querySelector<HTMLButtonElement>('[data-download]')!.disabled).toBe(false);

    const createObjectURL = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:x');
    vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    element.querySelector<HTMLButtonElement>('[data-download]')!.click();
    backend.expectOne('/api/painel/privacidade/exportacoes/e1/arquivo').flush(new Blob(['zip']));
    await settle(fixture);

    expect(createObjectURL).toHaveBeenCalled();
    expect(click).toHaveBeenCalled();
    expect(changed).toBe(2);
  });
});
