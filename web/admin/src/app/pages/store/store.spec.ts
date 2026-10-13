import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AuthStore } from '../../core/auth';
import { StorePage } from './store';
import { StoreProfile } from './store.api';

const profile = (customStorefront: boolean): StoreProfile =>
  ({
    slug: 'demo', cnpj: '11222333000181', legalName: 'Demo Ltda', tradeName: 'Loja Demo',
    theme: { primaryColor: '#1F5FBF', onPrimaryColor: '#FFFFFF', backgroundColor: '#FFFFFF', textColor: '#1D2330' },
    texts: { about: 'Sobre', returnPolicy: '7 dias', footer: 'Rua A' },
    warnings: [], logoId: null, hideOutOfStock: false,
    shipping: { originPostalCode: null, pickupEnabled: false, pickupAddress: null },
    customStorefront,
  }) as unknown as StoreProfile;

async function settle(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }) {
  for (let i = 0; i < 3; i++) {
    await fixture.whenStable();
    fixture.detectChanges();
  }
}

describe('StorePage — loja personalizada', () => {
  async function render(custom: boolean) {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: AuthStore, useValue: { can: () => false } }],
    });
    const fixture = TestBed.createComponent(StorePage);
    fixture.detectChanges();
    const backend = TestBed.inject(HttpTestingController);
    backend.expectOne('/api/painel/loja').flush(profile(custom));
    await settle(fixture);
    return { fixture, backend, element: fixture.nativeElement as HTMLElement };
  }

  it('mostra o selo, trava a aparência e ainda salva entrega com os valores atuais', async () => {
    const { fixture, backend, element } = await render(true);
    expect(element.querySelector('[data-custom-badge]')!.textContent).toContain('Loja personalizada');
    expect(element.querySelector('[data-logo-upload]')).toBeNull();
    expect(element.querySelector<HTMLInputElement>('input[formControlName=tradeName]')!.disabled).toBe(true);
    expect(element.querySelector<HTMLTextAreaElement>('textarea[formControlName=footer]')!.disabled).toBe(true);

    const pickup = element.querySelector<HTMLInputElement>('input[formControlName=pickupEnabled]')!;
    expect(pickup.disabled).toBe(false);
    pickup.click();
    await settle(fixture);
    const address = element.querySelector<HTMLTextAreaElement>('textarea[formControlName=pickupAddress]')!;
    address.value = 'Balcão';
    address.dispatchEvent(new Event('input'));
    await settle(fixture);
    (element.querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    const save = backend.expectOne({ method: 'PUT', url: '/api/painel/loja' });
    expect(save.request.body.tradeName).toBe('Loja Demo');
    expect(save.request.body.footer).toBe('Rua A');
    expect(save.request.body.shipping).toEqual({ originPostalCode: null, pickupEnabled: true, pickupAddress: 'Balcão' });
  });

  it('loja comum edita a aparência normalmente', async () => {
    const { element } = await render(false);
    expect(element.querySelector('[data-custom-badge]')).toBeNull();
    expect(element.querySelector('[data-logo-upload]')).not.toBeNull();
    expect(element.querySelector<HTMLInputElement>('input[formControlName=tradeName]')!.disabled).toBe(false);
  });
});
