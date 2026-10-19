import { Component, inject, signal } from '@angular/core';
import { Router, RouterLink, RouterOutlet } from '@angular/router';
import { Icon } from '../layout/icon';
import { NavGroup, SideNav } from '../layout/side-nav';
import { UserMenu } from '../layout/user-menu';
import { PlatformAuthStore } from './platform-auth';

/** "Lojas" fica marcado na lista e nas páginas de uma loja (/plataforma/lojas/...). */
const storesActive = (url: string) => url === '/plataforma' || url.startsWith('/plataforma/lojas/');

/** Menu do painel da plataforma. */
export const PLATFORM_MENU: NavGroup[] = [
  { id: 'lojas', label: 'Lojas', icon: 'stores', items: [{ label: 'Lojas', link: '/plataforma', active: storesActive }] },
  {
    id: 'gestao', label: 'Gestão', icon: 'manage',
    items: [
      { label: 'Planos', link: '/plataforma/planos' },
      { label: 'Auditoria', link: '/plataforma/auditoria' },
    ],
  },
];

/** Moldura do painel da plataforma: separada da moldura das lojas, sem nada de loja escolhida. */
@Component({
  selector: 'app-platform-shell',
  imports: [RouterOutlet, RouterLink, SideNav, UserMenu, Icon],
  template: `
    <header class="topbar platform">
      <button type="button" class="menu-toggle" [attr.aria-expanded]="drawerOpen()" aria-controls="side-nav" (click)="drawerOpen.set(!drawerOpen())"
        [attr.aria-label]="drawerOpen() ? 'Fechar menu' : 'Abrir menu'" data-menu-toggle>
        <app-icon [name]="drawerOpen() ? 'close' : 'menu'" />
      </button>
      <a routerLink="/plataforma" class="brand">Plataforma</a>
      <app-user-menu [email]="auth.current()?.admin?.email" subtitle="Superadmin">
        <button type="button" class="menu-item" role="menuitem" (click)="logout()" data-logout>Sair</button>
      </app-user-menu>
    </header>
    <div class="layout">
      <app-side-nav id="side-nav" [groups]="menu" [open]="drawerOpen()" (closed)="drawerOpen.set(false)" />
      @if (drawerOpen()) {
        <div class="drawer-backdrop" (click)="drawerOpen.set(false)" aria-hidden="true"></div>
      }
      <main class="content">
        <router-outlet />
      </main>
    </div>
  `,
})
export class PlatformShell {
  protected readonly auth = inject(PlatformAuthStore);
  private readonly router = inject(Router);
  protected readonly menu = PLATFORM_MENU;
  protected readonly drawerOpen = signal(false);

  async logout(): Promise<void> {
    await this.auth.logout();
    await this.router.navigateByUrl('/plataforma/entrar');
  }
}
