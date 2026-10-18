import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AuthStore } from '../../core/auth';
import { SyncDiagnosis, SyncGuardianSection } from './sync-guardian';

async function settle(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }) {
  for (let i = 0; i < 3; i++) {
    await fixture.whenStable();
    fixture.detectChanges();
  }
}

describe('SyncGuardianSection (RF33)', () => {
  const diagnosis: SyncDiagnosis = {
    key: 'acesso:mercado-livre',
    kind: 'channelReauth',
    title: 'Mercado Livre: a conexão perdeu a autorização',
    occurrences: 1,
    lastAt: '2026-10-18T12:00:00Z',
    lastError: 'Token revogado',
    explanation: 'A loja precisa autorizar de novo o acesso a Mercado Livre.',
    action: 'reconnect',
    actionText: 'Abra Canais de venda e conecte a conta de novo.',
  };

  function setup(canManage: boolean) {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: AuthStore, useValue: { can: () => canManage } }],
    });
    const fixture = TestBed.createComponent(SyncGuardianSection);
    fixture.detectChanges();
    return { fixture, backend: TestBed.inject(HttpTestingController), el: fixture.nativeElement as HTMLElement };
  }

  it('mostra o problema com a ação sugerida e o Dono desliga o aviso', async () => {
    const { fixture, backend, el } = setup(true);
    backend.expectOne('/api/painel/integracoes/diagnostico').flush([diagnosis]);
    backend.expectOne({ method: 'GET', url: '/api/painel/guardiao' }).flush({ enabled: true });
    await settle(fixture);

    const text = el.querySelector('[data-diagnosis="channelReauth"]')!.textContent!;
    expect(text).toContain('Mercado Livre: a conexão perdeu a autorização');
    expect(text).toContain('O que fazer: Abra Canais de venda e conecte a conta de novo.');
    const toggle = el.querySelector<HTMLInputElement>('[data-guardian-enabled]')!;
    expect(toggle.checked).toBe(true);

    toggle.click();
    await settle(fixture);
    const request = backend.expectOne({ method: 'PUT', url: '/api/painel/guardiao' });
    expect(request.request.body).toEqual({ ativo: false });
    request.flush({ enabled: false });
    await settle(fixture);
    expect(el.querySelector('[role=status]')!.textContent).toContain('Aviso por e-mail desligado.');
    backend.verify();
  });

  it('Operador vê o diagnóstico, sem a opção do aviso', async () => {
    const { fixture, backend, el } = setup(false);
    backend.expectOne('/api/painel/integracoes/diagnostico').flush([]);
    await settle(fixture);
    expect(el.querySelector('[data-no-problems]')!.textContent).toContain('Nenhum problema na sincronização agora.');
    expect(el.querySelector('[data-guardian-enabled]')).toBeNull();
    backend.verify();
  });
});
