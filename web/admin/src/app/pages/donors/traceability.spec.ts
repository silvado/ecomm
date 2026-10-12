import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { PartTraceability } from '../parts/part-traceability';
import { PartView } from '../parts/parts.api';
import { TraceabilitySettingsSection } from '../store/traceability-settings';
import { DonorFormPage } from './donor-form';

const settings = { dismantlerMode: true, fields: [{ key: 'detran', label: 'Registro Detran', required: true }] };

function part(extra: Partial<PartView['traceability']> = {}): PartView {
  return {
    id: 'p1',
    internalCode: 'K-1',
    title: 'Farol',
    condition: 'used',
    price: 100,
    status: 'draft',
    stock: { onHand: 1, reserved: 0, available: 1 },
    hasShippingDimensions: false,
    updatedAt: '2026-10-11T12:00:00Z',
    coverPhotoId: null,
    description: '',
    lengthCm: null,
    widthCm: null,
    heightCm: null,
    weightG: null,
    oemCodes: [],
    createdAt: '2026-10-11T12:00:00Z',
    photos: [],
    compatibilities: [],
    traceability: { donorVehicleId: null, donorVehicleLabel: null, tag: null, customFields: {}, missingForSale: [], ...extra },
  };
}

async function settle(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }) {
  await fixture.whenStable();
  fixture.detectChanges();
  await fixture.whenStable();
}

function type(element: HTMLElement, selector: string, value: string): void {
  const input = element.querySelector<HTMLInputElement>(selector)!;
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

describe('Rastreabilidade (RF10)', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] }));

  it('seção da peça mostra o que falta, liga o veículo doador e envia etiqueta e campos', async () => {
    const fixture = TestBed.createComponent(PartTraceability);
    fixture.componentRef.setInput('part', part({ missingForSale: ['veículo doador', 'Registro Detran'] }));
    const changed: PartView[] = [];
    fixture.componentInstance.changed.subscribe((p) => changed.push(p));
    fixture.detectChanges();
    const backend = TestBed.inject(HttpTestingController);
    backend.expectOne('/api/painel/rastreabilidade').flush(settings);
    await settle(fixture);
    const element = fixture.nativeElement as HTMLElement;

    expect(element.querySelector('[data-missing]')!.textContent).toContain('veículo doador; Registro Detran');

    type(element, 'input[type=search]', 'gol');
    await new Promise((resolve) => setTimeout(resolve, 350));
    backend.expectOne((r) => r.url === '/api/painel/doadores' && r.params.get('busca') === 'gol')
      .flush({ items: [{ id: 'd1', label: 'Volkswagen Gol 2011 · ABC1D23' }], total: 1, page: 1, pageSize: 8 });
    await settle(fixture);
    element.querySelector<HTMLButtonElement>('[data-donor="d1"]')!.click();
    await settle(fixture);
    expect(element.textContent).toContain('Volkswagen Gol 2011 · ABC1D23');

    type(element, 'input[name=tag]', ' et-1 ');
    type(element, '[data-field="detran"]', ' SP-1 ');
    await settle(fixture);
    element.querySelector<HTMLButtonElement>('[data-save-traceability]')!.click();

    const save = backend.expectOne({ method: 'PUT', url: '/api/painel/pecas/p1/rastreabilidade' });
    expect(save.request.body).toEqual({ donorVehicleId: 'd1', tag: 'et-1', customFields: { detran: 'SP-1' } });
    save.flush(part({ donorVehicleId: 'd1', tag: 'ET-1' }));
    await settle(fixture);
    expect(changed.length).toBe(1);
    backend.verify();
  });

  it('configuração da loja liga o modo desmontadora e adiciona campo', async () => {
    const fixture = TestBed.createComponent(TraceabilitySettingsSection);
    fixture.detectChanges();
    const backend = TestBed.inject(HttpTestingController);
    backend.expectOne('/api/painel/rastreabilidade').flush({ dismantlerMode: false, fields: [] });
    await settle(fixture);
    const element = fixture.nativeElement as HTMLElement;

    element.querySelector<HTMLInputElement>('[data-dismantler]')!.click();
    element.querySelector<HTMLButtonElement>('[data-add-field]')!.click();
    await settle(fixture);
    type(element, 'input[placeholder^="Ex.:"]', ' Registro Detran ');
    await settle(fixture);
    element.querySelector<HTMLButtonElement>('[data-save-settings]')!.click();

    const save = backend.expectOne({ method: 'PUT', url: '/api/painel/rastreabilidade' });
    expect(save.request.body).toEqual({ dismantlerMode: true, fields: [{ key: '', label: 'Registro Detran', required: false }] });
    save.flush(settings);
    await settle(fixture);
    expect(element.querySelector('[role=status]')!.textContent).toContain('salva');
    backend.verify();
  });

  it('cadastro do veículo envia campos vazios como nulos e mostra a recusa da API', async () => {
    const fixture = TestBed.createComponent(DonorFormPage);
    fixture.detectChanges();
    await settle(fixture);
    const backend = TestBed.inject(HttpTestingController);
    const element = fixture.nativeElement as HTMLElement;

    type(element, 'input[formControlName=brand]', 'Volkswagen');
    type(element, 'input[formControlName=model]', 'Gol');
    type(element, 'input[formControlName=plate]', 'abc1d23');
    type(element, 'input[formControlName=renavam]', '00639884963');
    await settle(fixture);
    element.querySelector<HTMLButtonElement>('button[type=submit]')!.click();

    const create = backend.expectOne({ method: 'POST', url: '/api/painel/doadores' });
    expect(create.request.body).toEqual({
      brand: 'Volkswagen', model: 'Gol', modelYear: null, plate: 'abc1d23', chassis: null, renavam: '00639884963', color: null,
      deregistrationCertificate: null, entryDate: null, entryInvoiceKey: null, notes: null,
    });
    create.flush({ title: 'Renavam inválido: confira os dígitos.' }, { status: 400, statusText: 'Bad Request' });
    await settle(fixture);
    expect(element.querySelector('[role=alert]')!.textContent).toContain('Renavam inválido');
    backend.verify();
  });
});
