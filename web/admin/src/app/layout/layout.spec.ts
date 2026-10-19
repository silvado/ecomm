import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { AuthStore, Permission, Session } from '../core/auth';
import { PlatformShell } from '../platform/platform-shell';
import { Shell } from './shell';
import { initials, UserMenu } from './user-menu';

@Component({ template: '' })
class Blank {}

const ALL: Permission[] = ['catalogManage', 'ordersManage', 'supportManage', 'vaultManage', 'planManage', 'usersManage', 'dataExport', 'storeManage'];

const session = (permissions: Permission[], role: 'owner' | 'operator', tenants = 1): Session => {
  const tenant = { id: 't1', slug: 'loja', name: 'Auto Peças Silva', role, permissions };
  return {
    accessToken: 'token', expiresAt: '2026-10-19T12:00:00Z', userId: 'u1', email: 'maria.souza@pecas.test', mustChangePassword: false,
    tenant, tenants: Array.from({ length: tenants }, (_, i) => ({ ...tenant, id: 't' + (i + 1) })),
  };
};

async function settle(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }) {
  for (let i = 0; i < 3; i++) {
    await fixture.whenStable();
    fixture.detectChanges();
  }
}

describe('Moldura do painel', () => {
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([{ path: '**', component: Blank }])],
    });
    backend = TestBed.inject(HttpTestingController);
  });

  /** Entra com a sessão e abre a moldura numa rota; os avisos do topo (assinatura, cofre, integrações) ficam sem resposta. */
  async function render(s: Session, url = '/') {
    const login = TestBed.inject(AuthStore).login('maria.souza@pecas.test', 'senha');
    backend.expectOne('/api/auth/login').flush(s);
    await login;
    await TestBed.inject(Router).navigateByUrl(url);
    const fixture = TestBed.createComponent(Shell);
    await settle(fixture);
    backend.match(() => true);
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  const groups = (element: HTMLElement) => [...element.querySelectorAll('[data-group]')].map((g) => g.getAttribute('data-group'));
  const links = (element: HTMLElement) => [...element.querySelectorAll('.side-nav a')].map((a) => a.textContent!.trim());

  it('o Dono vê todos os grupos; o grupo da página atual abre sozinho com o item marcado', async () => {
    const { element } = await render(session(ALL, 'owner'), '/pecas/0199-abc');
    expect(groups(element)).toEqual(['inicio', 'catalogo', 'vendas', 'canais', 'configuracoes']);
    expect(links(element)).toEqual(['Início', 'Peças', 'Nova peça', 'Importar planilha', 'Veículos doadores']);
    expect(element.querySelector('.side-nav a.active')!.textContent!.trim()).toBe('Peças'); // edição de uma peça marca "Peças"
    expect(element.querySelector('[data-group="vendas"] button')!.getAttribute('aria-expanded')).toBe('false');
  });

  it('abrir e fechar um grupo à mão; ao navegar, o grupo da nova página abre', async () => {
    const { fixture, element } = await render(session(ALL, 'owner'), '/pecas/nova');
    expect(element.querySelector('.side-nav a.active')!.textContent!.trim()).toBe('Nova peça');

    (element.querySelector('[data-group="catalogo"] button') as HTMLButtonElement).click();
    await settle(fixture);
    expect(element.querySelector('[data-group="catalogo"] .sub')).toBeNull(); // fechou e não reabre sozinho

    await TestBed.inject(Router).navigateByUrl('/usuarios');
    await settle(fixture);
    expect(links(element)).toContain('Usuários');
    expect(element.querySelector('.side-nav a.active')!.textContent!.trim()).toBe('Usuários');
  });

  it('o Operador só vê o que o perfil permite; grupo vazio some', async () => {
    const { fixture, element } = await render(session(['catalogManage', 'ordersManage'], 'operator'), '/pedidos');
    expect(groups(element)).toEqual(['inicio', 'catalogo', 'vendas', 'canais']);
    // Sem "Perguntas"; em Canais só "Integrações", que vira link direto.
    expect(links(element)).toEqual(['Início', 'Pedidos', 'Balcão', 'Integrações']);

    (element.querySelector('[data-user-menu]') as HTMLButtonElement).click();
    await settle(fixture);
    const actions = [...element.querySelectorAll('.user-panel .menu-item')].map((a) => a.textContent!.trim());
    expect(actions).toEqual(['Trocar senha', 'Sair']); // sem Assinatura e sem Trocar de loja
    expect(element.querySelector('.user-head')!.textContent).toContain('Operador');
  });

  it('a área do usuário mostra as iniciais e as ações do Dono com mais de uma loja', async () => {
    const { fixture, element } = await render(session(ALL, 'owner', 2));
    expect(element.querySelector('.avatar')!.textContent!.trim()).toBe('MS');
    (element.querySelector('[data-user-menu]') as HTMLButtonElement).click();
    await settle(fixture);
    const actions = [...element.querySelectorAll('.user-panel .menu-item')].map((a) => a.textContent!.trim());
    expect(actions).toEqual(['Trocar de loja', 'Assinatura', 'Trocar senha', 'Sair']);
  });

  it('no celular, o botão abre a gaveta e escolher um item fecha', async () => {
    const { fixture, element } = await render(session(ALL, 'owner'), '/pedidos');
    const toggle = element.querySelector('[data-menu-toggle]') as HTMLButtonElement;
    toggle.click();
    await settle(fixture);
    expect(element.querySelector('.side-nav')!.classList).toContain('open');
    expect(toggle.getAttribute('aria-label')).toBe('Fechar menu');

    (element.querySelector('.side-nav a[href="/balcao"]') as HTMLAnchorElement).click();
    await settle(fixture);
    expect(element.querySelector('.side-nav')!.classList).not.toContain('open');
  });
});

@Component({
  imports: [UserMenu],
  template: `
    <app-user-menu email="dono@loja.test" subtitle="Dono">
      <button type="button" class="menu-item">Trocar senha</button>
    </app-user-menu>
    <p data-outside>fora</p>
  `,
})
class Host {}

describe('Área do usuário', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideRouter([])] }));

  it('fecha com Esc (devolvendo o foco ao botão), clique fora e ao escolher uma ação', async () => {
    const fixture = TestBed.createComponent(Host);
    await settle(fixture);
    const element = fixture.nativeElement as HTMLElement;
    document.body.appendChild(element);
    const trigger = element.querySelector('[data-user-menu]') as HTMLButtonElement;
    const open = async () => {
      trigger.click();
      await settle(fixture);
      expect(trigger.getAttribute('aria-expanded')).toBe('true');
    };

    await open();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    await settle(fixture);
    expect(element.querySelector('.user-panel')).toBeNull();
    expect(document.activeElement).toBe(trigger);

    await open();
    (element.querySelector('[data-outside]') as HTMLElement).click();
    await settle(fixture);
    expect(element.querySelector('.user-panel')).toBeNull();

    await open();
    (element.querySelector('.user-panel .menu-item') as HTMLButtonElement).click();
    await settle(fixture);
    expect(element.querySelector('.user-panel')).toBeNull();
    element.remove();
  });

  it('iniciais pelo e-mail', () => {
    expect(initials('maria.souza@x.test')).toBe('MS');
    expect(initials('joao@x.test')).toBe('JO');
    expect(initials('')).toBe('?');
  });
});

describe('Moldura da plataforma', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([{ path: '**', component: Blank }])],
    }),
  );

  it('menu com Lojas e Gestão; a página de uma loja marca "Lojas"', async () => {
    await TestBed.inject(Router).navigateByUrl('/plataforma/lojas/0199/tema');
    const fixture = TestBed.createComponent(PlatformShell);
    await settle(fixture);
    const element = fixture.nativeElement as HTMLElement;
    expect([...element.querySelectorAll('[data-group]')].map((g) => g.getAttribute('data-group'))).toEqual(['lojas', 'gestao']);
    expect(element.querySelector('.side-nav a.active')!.textContent!.trim()).toBe('Lojas');
    expect(element.querySelector('.user-trigger small')!.textContent).toContain('Superadmin');
  });
});
