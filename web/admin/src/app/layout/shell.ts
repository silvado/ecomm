import { Component, computed, DestroyRef, inject, OnInit, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';
import { AuthStore, Permission } from '../core/auth';
import { IntegrationsApi } from '../pages/integrations/integrations.api';
import { standingText, SubscriptionApi } from '../pages/subscription/subscription.api';
import { alertText, CertificateAlert, VaultApi } from '../pages/vault/vault.api';
import { RoleLabelPipe } from '../shared/role-label';
import { Icon } from './icon';
import { NavGroup, NavItem, SideNav } from './side-nav';
import { UserMenu } from './user-menu';

/** De quanto em quanto tempo o painel confere se há falhas de integração pendentes (RF28 CA2). */
const FAILURE_CHECK_MS = 2 * 60 * 1000;

/** "Peças" fica marcado na lista e na edição de uma peça, mas não em "Nova peça" nem em "Importar planilha". */
const partsActive = (url: string) => url === '/pecas' || (/^\/pecas\/[^/]+$/.test(url) && !['/pecas/nova', '/pecas/importar'].includes(url));

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RoleLabelPipe, SideNav, UserMenu, Icon],
  template: `
    <header class="topbar">
      <button type="button" class="menu-toggle" [attr.aria-expanded]="drawerOpen()" aria-controls="side-nav" (click)="drawerOpen.set(!drawerOpen())"
        [attr.aria-label]="drawerOpen() ? 'Fechar menu' : 'Abrir menu'" data-menu-toggle>
        <app-icon [name]="drawerOpen() ? 'close' : 'menu'" />
      </button>
      <a routerLink="/" class="brand">{{ auth.tenant()?.name }}</a>
      <app-user-menu [email]="auth.current()?.email" [subtitle]="(auth.tenant()?.role | roleLabel) ?? ''">
        @if ((auth.current()?.tenants?.length ?? 0) > 1) {
          <a routerLink="/escolher-loja" class="menu-item" role="menuitem">Trocar de loja</a>
        }
        @if (auth.can('planManage')) {
          <a routerLink="/assinatura" class="menu-item" role="menuitem">Assinatura</a>
        }
        <a routerLink="/trocar-senha" class="menu-item" role="menuitem">Trocar senha</a>
        <button type="button" class="menu-item" role="menuitem" (click)="logout()" data-logout>Sair</button>
      </app-user-menu>
    </header>
    <div class="layout">
      <app-side-nav id="side-nav" [groups]="menu()" [open]="drawerOpen()" (closed)="drawerOpen.set(false)" />
      @if (drawerOpen()) {
        <div class="drawer-backdrop" (click)="drawerOpen.set(false)" aria-hidden="true"></div>
      }
      <main class="content">
        @if (billingAlert(); as text) {
          <p class="alert-bar" role="alert" data-billing-alert>
            {{ text }}
            @if (auth.can('planManage')) {
              <a routerLink="/assinatura">Ver faturas</a>
            }
          </p>
        }
        @if (pendingFailures(); as n) {
          <p class="alert-bar" role="alert" data-failure-alert>
            {{ n === 1 ? '1 integração falhou' : n + ' integrações falharam' }} e precisa{{ n === 1 ? '' : 'm' }} de atenção.
            <a routerLink="/integracoes">Ver e reprocessar</a>
          </p>
        }
        @if (channelsNeedingReauth()) {
          <p class="alert-bar" role="alert" data-reauth-alert>
            A conexão com o Mercado Livre precisa ser autorizada de novo: os anúncios não estão sendo atualizados.
            @if (auth.can('storeManage')) {
              <a routerLink="/canais">Abrir Canais de venda</a>
            } @else {
              Avise o Dono da loja.
            }
          </p>
        }
        @if (certificateAlert(); as a) {
          <p class="alert-bar" role="alert" data-certificate-alert>
            {{ certificateText(a) }} <a routerLink="/cofre">Abrir o Cofre</a>
          </p>
        }
        <router-outlet />
      </main>
    </div>
  `,
})
export class Shell implements OnInit {
  protected readonly auth = inject(AuthStore);
  private readonly router = inject(Router);
  private readonly integrations = inject(IntegrationsApi);
  private readonly vault = inject(VaultApi);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly pendingFailures = signal(0);
  /** RF21 CA2: canal que pede nova autorização (o ML recusou a renovação). */
  protected readonly channelsNeedingReauth = signal(0);
  protected readonly certificateAlert = signal<CertificateAlert | null>(null);
  protected readonly certificateText = alertText;
  private readonly subscription = inject(SubscriptionApi);
  /** RF38: fatura vencida, painel somente leitura ou loja suspensa — para todos os perfis. */
  protected readonly billingAlert = signal<string | null>(null);
  /** Menu no celular (gaveta). */
  protected readonly drawerOpen = signal(false);

  /** Menu por grupos; cada item só aparece para quem tem a permissão. */
  protected readonly menu = computed<NavGroup[]>(() => {
    this.auth.current(); // recalcula ao trocar de loja ou de perfil
    const only = (permission: Permission, item: NavItem) => (this.auth.can(permission) ? [item] : []);
    return [
      { id: 'inicio', label: 'Início', icon: 'home', items: [{ label: 'Início', link: '/' }] },
      {
        id: 'catalogo', label: 'Catálogo', icon: 'catalog',
        items: [
          ...only('catalogManage', { label: 'Peças', link: '/pecas', active: partsActive }),
          ...only('catalogManage', { label: 'Nova peça', link: '/pecas/nova' }),
          ...only('catalogManage', { label: 'Importar planilha', link: '/pecas/importar' }),
          ...only('catalogManage', { label: 'Veículos doadores', link: '/doadores' }),
        ],
      },
      {
        id: 'vendas', label: 'Vendas', icon: 'sales',
        items: [
          ...only('ordersManage', { label: 'Pedidos', link: '/pedidos' }),
          ...only('ordersManage', { label: 'Balcão', link: '/balcao' }),
          ...only('supportManage', { label: 'Perguntas', link: '/perguntas' }),
        ],
      },
      {
        id: 'canais', label: 'Canais', icon: 'channels',
        items: [
          ...only('storeManage', { label: 'Canais de venda', link: '/canais' }),
          ...only('ordersManage', { label: 'Integrações', link: '/integracoes' }),
        ],
      },
      {
        id: 'configuracoes', label: 'Configurações', icon: 'settings',
        items: [
          ...only('storeManage', { label: 'Primeiros passos', link: '/primeiros-passos' }),
          ...only('storeManage', { label: 'Dados da loja', link: '/loja' }),
          ...only('vaultManage', { label: 'Pagamentos', link: '/pagamentos' }),
          ...only('vaultManage', { label: 'Cofre', link: '/cofre' }),
          ...only('usersManage', { label: 'Usuários', link: '/usuarios' }),
          ...only('dataExport', { label: 'Privacidade', link: '/privacidade' }),
        ],
      },
    ];
  });

  ngOnInit(): void {
    void this.checkBilling();
    // Depois de pagar (volta do gateway) ou de navegar, a faixa acompanha a situação.
    this.router.events.pipe(filter((e) => e instanceof NavigationEnd), takeUntilDestroyed(this.destroyRef)).subscribe(() => void this.checkBilling());
    // RF06 CA5: o Dono vê o aviso de vencimento do A1 em qualquer página (uma vez por entrada no painel).
    if (this.auth.can('vaultManage'))
      this.vault.alert().then((a) => this.certificateAlert.set(a), () => undefined);
    if (!this.auth.can('ordersManage')) return;
    void this.checkFailures();
    const timer = setInterval(() => void this.checkFailures(), FAILURE_CHECK_MS);
    this.destroyRef.onDestroy(() => clearInterval(timer));
    // Depois de reprocessar na tela de integrações, a faixa some ao navegar.
    this.router.events.pipe(filter((e) => e instanceof NavigationEnd), takeUntilDestroyed(this.destroyRef)).subscribe(() => void this.checkFailures());
  }

  async logout(): Promise<void> {
    await this.auth.logout();
    await this.router.navigateByUrl('/entrar');
  }

  private async checkBilling(): Promise<void> {
    try {
      this.billingAlert.set(standingText(await this.subscription.standing()));
    } catch {
      // Só um aviso: se a consulta falhar, o painel segue normal.
    }
  }

  private async checkFailures(): Promise<void> {
    try {
      const summary = await this.integrations.summary();
      this.pendingFailures.set(summary.pendingFailures);
      this.channelsNeedingReauth.set(summary.channelsNeedingReauth ?? 0);
    } catch {
      // Aviso é só um atalho: se a consulta falhar, o painel segue normal.
    }
  }
}
