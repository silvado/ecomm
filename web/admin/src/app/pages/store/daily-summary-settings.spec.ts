import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { DailySummarySettingsSection } from './daily-summary-settings';

async function settle(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }) {
  for (let i = 0; i < 3; i++) {
    await fixture.whenStable();
    fixture.detectChanges();
  }
}

describe('DailySummarySettingsSection (RF34)', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] }));

  async function render() {
    const fixture = TestBed.createComponent(DailySummarySettingsSection);
    fixture.detectChanges();
    const backend = TestBed.inject(HttpTestingController);
    backend.expectOne('/api/painel/resumo-diario').flush({ enabled: true, hour: 7 });
    await settle(fixture);
    return { fixture, backend, el: fixture.nativeElement as HTMLElement };
  }

  it('mostra o padrão (ligado às 7h) e salva a nova hora', async () => {
    const { fixture, backend, el } = await render();
    const select = el.querySelector<HTMLSelectElement>('[data-hour]')!;
    expect(el.querySelector<HTMLInputElement>('[data-enabled]')!.checked).toBe(true);
    expect(select.options[select.selectedIndex].textContent!.trim()).toBe('07:00');

    select.value = select.options[18].value;
    select.dispatchEvent(new Event('change'));
    await settle(fixture);
    el.querySelector<HTMLButtonElement>('[data-save]')!.click();
    const request = backend.expectOne({ method: 'PUT', url: '/api/painel/resumo-diario' });
    expect(request.request.body).toEqual({ ativo: true, hora: 18 });
    request.flush({ enabled: true, hour: 18 });
    await settle(fixture);
    expect(el.querySelector('[role=status]')!.textContent).toContain('Resumo diário salvo.');
  });

  it('a prévia mostra os números de ontem', async () => {
    const { fixture, backend, el } = await render();
    el.querySelector<HTMLButtonElement>('[data-preview]')!.click();
    backend.expectOne('/api/painel/resumo-diario/previa').flush({
      day: '2026-10-17',
      sales: [{ origin: 'counter', orders: 2, total: 240 }],
      staleParts: 4,
      pendingQuestions: 1,
    });
    await settle(fixture);
    const text = el.querySelector('[data-preview-result]')!.textContent!;
    expect(text).toContain('Balcão: 2 pedidos');
    expect(text).toContain('Perguntas sem resposta: 1');
    expect(text).toContain('Peças paradas há mais de 90 dias: 4');
  });
});
