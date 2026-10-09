import { Component, inject } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthStore } from '../core/auth';
import { RoleLabelPipe } from '../shared/role-label';

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, RoleLabelPipe],
  template: `
    <header class="topbar">
      <div class="store">
        <strong>{{ auth.tenant()?.name }}</strong>
        <span class="muted">{{ auth.tenant()?.role | roleLabel }}</span>
      </div>
      <nav>
        <a routerLink="/" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: true }">Início</a>
        @if (auth.can('catalogManage')) {
          <a routerLink="/pecas" routerLinkActive="active">Peças</a>
        }
        @if (auth.can('storeManage')) {
          <a routerLink="/loja" routerLinkActive="active">Dados da loja</a>
        }
        @if (auth.can('usersManage')) {
          <a routerLink="/usuarios" routerLinkActive="active">Usuários</a>
        }
      </nav>
      <div class="account">
        <span class="muted">{{ auth.current()?.email }}</span>
        @if ((auth.current()?.tenants?.length ?? 0) > 1) {
          <a routerLink="/escolher-loja">Trocar de loja</a>
        }
        <a routerLink="/trocar-senha">Trocar senha</a>
        <button type="button" class="link" (click)="logout()">Sair</button>
      </div>
    </header>
    <router-outlet />
  `,
})
export class Shell {
  protected readonly auth = inject(AuthStore);
  private readonly router = inject(Router);

  async logout(): Promise<void> {
    await this.auth.logout();
    await this.router.navigateByUrl('/entrar');
  }
}
