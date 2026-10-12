import { Component, DestroyRef, inject, OnInit, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';
import { AuthStore } from '../core/auth';
import { IntegrationsApi } from '../pages/integrations/integrations.api';
import { RoleLabelPipe } from '../shared/role-label';

/** De quanto em quanto tempo o painel confere se há falhas de integração pendentes (RF28 CA2). */
const FAILURE_CHECK_MS = 2 * 60 * 1000;

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
          <a routerLink="/doadores" routerLinkActive="active">Doadores</a>
        }
        @if (auth.can('ordersManage')) {
          <a routerLink="/pedidos" routerLinkActive="active">Pedidos</a>
          <a routerLink="/balcao" routerLinkActive="active">Balcão</a>
          <a routerLink="/integracoes" routerLinkActive="active">Integrações</a>
        }
        @if (auth.can('storeManage')) {
          <a routerLink="/loja" routerLinkActive="active">Dados da loja</a>
        }
        @if (auth.can('vaultManage')) {
          <a routerLink="/pagamentos" routerLinkActive="active">Pagamentos</a>
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
    @if (pendingFailures(); as n) {
      <p class="alert-bar" role="alert" data-failure-alert>
        {{ n === 1 ? '1 integração falhou' : n + ' integrações falharam' }} e precisa{{ n === 1 ? '' : 'm' }} de atenção.
        <a routerLink="/integracoes">Ver e reprocessar</a>
      </p>
    }
    <router-outlet />
  `,
})
export class Shell implements OnInit {
  protected readonly auth = inject(AuthStore);
  private readonly router = inject(Router);
  private readonly integrations = inject(IntegrationsApi);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly pendingFailures = signal(0);

  ngOnInit(): void {
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

  private async checkFailures(): Promise<void> {
    try {
      this.pendingFailures.set((await this.integrations.summary()).pendingFailures);
    } catch {
      // Aviso é só um atalho: se a consulta falhar, o painel segue normal.
    }
  }
}
