import { Component, inject } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { PlatformAuthStore } from './platform-auth';

/** Moldura do painel da plataforma: separada da moldura das lojas, sem nada de loja escolhida. */
@Component({
  selector: 'app-platform-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <header class="topbar platform">
      <div class="store">
        <strong>Plataforma</strong>
        <span class="muted">Superadmin</span>
      </div>
      <nav>
        <a routerLink="/plataforma" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: true }">Lojas</a>
        <a routerLink="/plataforma/planos" routerLinkActive="active">Planos</a>
        <a routerLink="/plataforma/auditoria" routerLinkActive="active">Auditoria</a>
      </nav>
      <div class="account">
        <span class="muted">{{ auth.current()?.admin?.email }}</span>
        <button type="button" class="link" (click)="logout()">Sair</button>
      </div>
    </header>
    <router-outlet />
  `,
})
export class PlatformShell {
  protected readonly auth = inject(PlatformAuthStore);
  private readonly router = inject(Router);

  async logout(): Promise<void> {
    await this.auth.logout();
    await this.router.navigateByUrl('/plataforma/entrar');
  }
}
