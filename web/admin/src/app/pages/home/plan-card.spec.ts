import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { PlanCard, PlanView } from './plan-card';

async function settle(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }) {
  for (let i = 0; i < 3; i++) {
    await fixture.whenStable();
    fixture.detectChanges();
  }
}

describe('Seu plano', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] }));

  async function render(plan: PlanView) {
    const fixture = TestBed.createComponent(PlanCard);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/painel/plano').flush(plan);
    await settle(fixture);
    return fixture.nativeElement as HTMLElement;
  }

  it('mostra uso, limites e recursos', async () => {
    const element = await render({
      code: 'essencial', name: 'Essencial',
      usage: [
        { key: 'active_parts', used: 1200, limit: 3000 },
        { key: 'users', used: 1, limit: 2 },
        { key: 'ai_listings_month', used: 0, limit: 50 },
      ],
      features: ['channel:mercado-livre', 'ai:part_from_photos'],
    });
    expect(element.querySelector('h2')!.textContent).toContain('Essencial');
    expect(element.querySelector('[data-usage="active_parts"]')!.textContent).toContain('1.200 de 3.000');
    expect(element.textContent).toContain('Mercado Livre');
    expect(element.querySelector('[data-plan-limit]')).toBeNull();
  });

  it('avisa quando chegou ao limite e mostra sem limite', async () => {
    const element = await render({
      code: 'completo', name: 'Completo',
      usage: [{ key: 'users', used: 10, limit: 10 }, { key: 'active_parts', used: 5000, limit: null }],
      features: [],
    });
    expect(element.querySelector('[data-usage="active_parts"]')!.textContent).toContain('(sem limite)');
    expect(element.querySelector('[data-plan-limit]')).not.toBeNull();
  });
});
