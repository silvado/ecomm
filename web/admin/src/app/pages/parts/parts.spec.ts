import { registerLocaleData } from '@angular/common';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import localePt from '@angular/common/locales/pt';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { PartsListPage } from './parts-list';
import { PartPage, parseOemCodes } from './parts.api';

registerLocaleData(localePt, 'pt-BR');

describe('parseOemCodes', () => {
  it('aceita um por linha, vírgula ou ponto e vírgula e ignora vazios', () => {
    expect(parseOemCodes('5U0 941 015\n\n 6R0.941.007 ; AAA1,BBB2 ')).toEqual(['5U0 941 015', '6R0.941.007', 'AAA1', 'BBB2']);
  });
});

describe('PartsListPage', () => {
  const page: PartPage = {
    items: [
      {
        id: 'p1',
        internalCode: 'FAR-001',
        title: 'Farol Gol G5',
        condition: 'used',
        price: 1350.5,
        status: 'active',
        stock: { onHand: 3, reserved: 1, available: 2 },
        hasShippingDimensions: false,
        updatedAt: '2026-10-09T12:00:00Z',
        coverPhotoId: null,
      },
    ],
    total: 1,
    page: 1,
    pageSize: 25,
  };

  it('lista as peças com preço em reais, estoque e aviso de medidas', async () => {
    TestBed.configureTestingModule({
      imports: [PartsListPage],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(PartsListPage);
    fixture.detectChanges();

    const request = TestBed.inject(HttpTestingController).expectOne((r) => r.url === '/api/painel/pecas');
    expect(request.request.params.get('pagina')).toBe('1');
    expect(request.request.params.has('busca')).toBe(false);
    request.flush(page);
    await fixture.whenStable();
    fixture.detectChanges();

    const row = (fixture.nativeElement as HTMLElement).querySelector('tbody tr')!.textContent!;
    expect(row).toContain('FAR-001');
    expect(row).toContain('R$');
    expect(row).toContain('1.350,50');
    expect(row).toContain('2 / 3');
    expect(row).toContain('sem medidas');
    expect(row).toContain('Ativa');
  });
});
