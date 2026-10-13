import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { VaultPage } from './vault';
import { alertText } from './vault.api';

async function settle(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }) {
  for (let i = 0; i < 3; i++) {
    await fixture.whenStable();
    fixture.detectChanges();
  }
}

describe('Cofre', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] }));

  it('mostra o certificado, o aviso de vencimento e os segredos sem valores', async () => {
    const fixture = TestBed.createComponent(VaultPage);
    fixture.detectChanges();
    const backend = TestBed.inject(HttpTestingController);
    backend.expectOne('/api/painel/cofre').flush([
      { kind: 'paymentGatewayCredential', name: 'mercado-pago:access-token', hint: 'ab12', expiresAt: null, updatedAt: '2026-10-01T12:00:00Z' },
      { kind: 'a1Certificate', name: 'a1:certificado', hint: '', expiresAt: '2026-10-22T12:00:00Z', updatedAt: '2026-10-01T12:00:00Z' },
    ]);
    backend.expectOne('/api/painel/cofre/certificado').flush({
      holder: 'LOJA DEMO LTDA', cnpj: '11222333000181', notBefore: '2025-10-22T12:00:00Z', expiresAt: '2026-10-22T12:00:00Z', daysLeft: 10,
      updatedAt: '2026-10-01T12:00:00Z',
    });
    backend.expectOne('/api/painel/cofre/alerta').flush({ expiresAt: '2026-10-22T12:00:00Z', daysLeft: 10, level: 'warning' });
    await settle(fixture);
    const element = fixture.nativeElement as HTMLElement;

    expect(element.querySelector('[data-holder]')!.textContent).toBe('LOJA DEMO LTDA');
    expect(element.querySelector('[data-certificate-alert]')!.textContent).toContain('vence em 10 dias');
    const rows = Array.from(element.querySelectorAll('[data-secret]')).map((r) => r.textContent!.replace(/\s+/g, ' '));
    expect(rows[0]).toContain('Mercado Pago — Access Token');
    expect(rows[0]).toContain('…ab12');
    expect(rows[1]).toContain('Certificado digital A1');
  });

  it('textos do aviso', () => {
    expect(alertText({ expiresAt: '2026-10-22T15:00:00Z', daysLeft: 0, level: 'urgent' })).toContain('vence hoje');
    expect(alertText({ expiresAt: '2026-10-22T15:00:00Z', daysLeft: 1, level: 'urgent' })).toContain('vence amanhã');
    expect(alertText({ expiresAt: '2026-10-22T15:00:00Z', daysLeft: -1, level: 'expired' })).toContain('venceu em 22/10/2026');
  });
});
