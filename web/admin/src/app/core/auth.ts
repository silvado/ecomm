import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { computed, inject, Injectable, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export type Permission =
  | 'catalogManage'
  | 'ordersManage'
  | 'supportManage'
  | 'vaultManage'
  | 'planManage'
  | 'usersManage'
  | 'dataExport';

export type Role = 'owner' | 'operator';

export interface TenantAccess {
  id: string;
  slug: string;
  name: string;
  role: Role;
  permissions: Permission[];
}

export interface Session {
  accessToken: string;
  expiresAt: string;
  userId: string;
  email: string;
  mustChangePassword: boolean;
  tenant: TenantAccess | null;
  tenants: TenantAccess[];
}

/** Erro da API (ProblemDetails) com a mensagem pronta para o usuário. */
export function problemTitle(error: unknown, fallback = 'Não foi possível concluir. Tente novamente.'): string {
  return error instanceof HttpErrorResponse && typeof error.error?.title === 'string' ? error.error.title : fallback;
}

const delay = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

/**
 * Sessão do painel (RF07). O token de acesso fica só em memória; o de renovação está num cookie HttpOnly
 * que o JavaScript não lê — recarregar a página renova a sessão pelo cookie.
 */
@Injectable({ providedIn: 'root' })
export class AuthStore {
  private readonly http = inject(HttpClient);
  private readonly session = signal<Session | null>(null);
  private refreshing: Promise<boolean> | null = null;

  readonly current = this.session.asReadonly();
  readonly accessToken = computed(() => this.session()?.accessToken ?? null);
  readonly tenant = computed(() => this.session()?.tenant ?? null);

  can(permission: Permission): boolean {
    return this.tenant()?.permissions.includes(permission) ?? false;
  }

  async login(email: string, password: string): Promise<Session> {
    return this.store(await firstValueFrom(this.http.post<Session>('/api/auth/login', { email, password })));
  }

  async selectTenant(tenantId: string): Promise<Session> {
    return this.store(await firstValueFrom(this.http.post<Session>('/api/auth/loja', { tenantId })));
  }

  async changePassword(currentPassword: string, newPassword: string): Promise<Session> {
    return this.store(await firstValueFrom(this.http.put<Session>('/api/auth/senha', { currentPassword, newPassword })));
  }

  async logout(): Promise<void> {
    try {
      await firstValueFrom(this.http.post('/api/auth/logout', null));
    } finally {
      this.session.set(null);
    }
  }

  /** Renova pelo cookie. Uma renovação por vez; se outra aba renovou junto (409), espera e repete. */
  refresh(): Promise<boolean> {
    this.refreshing ??= this.doRefresh().finally(() => (this.refreshing = null));
    return this.refreshing;
  }

  private async doRefresh(): Promise<boolean> {
    for (let attempt = 0; attempt < 3; attempt++) {
      try {
        this.store(await firstValueFrom(this.http.post<Session>('/api/auth/refresh', null)));
        return true;
      } catch (error) {
        if (error instanceof HttpErrorResponse && error.status === 409) {
          await delay(300 * (attempt + 1));
          continue;
        }
        this.session.set(null);
        return false;
      }
    }
    this.session.set(null);
    return false;
  }

  private store(session: Session): Session {
    this.session.set(session);
    return session;
  }
}
