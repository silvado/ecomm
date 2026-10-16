import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { LegalTextsSection } from './legal-texts';

async function settle(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }) {
  await fixture.whenStable();
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

describe('LegalTextsSection', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] }));

  it('mostra o modelo como sugestão, copia para editar e salva os dois textos', async () => {
    const fixture = TestBed.createComponent(LegalTextsSection);
    fixture.detectChanges();
    const backend = TestBed.inject(HttpTestingController);
    backend.expectOne('/api/painel/loja/privacidade').flush({ privacyPolicy: '', terms: 'Termos.', defaultPrivacyPolicy: 'Modelo de política', defaultTerms: 'Modelo de termos' });
    await settle(fixture);
    const element = fixture.nativeElement as HTMLElement;
    const privacy = element.querySelector<HTMLTextAreaElement>('[data-privacy]')!;
    expect(privacy.placeholder).toBe('Modelo de política');

    element.querySelector<HTMLButtonElement>('[data-use-privacy-template]')!.click();
    await settle(fixture);
    expect(privacy.value).toBe('Modelo de política');

    element.querySelector<HTMLButtonElement>('[data-save-legal]')!.click();
    const save = backend.expectOne('/api/painel/loja/privacidade');
    expect(save.request.method).toBe('PUT');
    expect(save.request.body).toEqual({ politica: 'Modelo de política', termos: 'Termos.' });
    save.flush({ privacyPolicy: 'Modelo de política', terms: 'Termos.', defaultPrivacyPolicy: 'Modelo de política', defaultTerms: 'Modelo de termos' });
    await settle(fixture);

    expect(element.querySelector('[role=status]')!.textContent).toContain('Textos salvos');
  });
});
