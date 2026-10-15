import { HttpClient, HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { computed, inject, Injectable, signal } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, firstValueFrom, from, switchMap, throwError } from 'rxjs';

/** Etapas do login do superadmin (ADR-0007): código do app, primeiro acesso (senha + app) ou sessão completa. */
export type PlatformStage = 'codigo' | 'cadastro' | 'completo';

export interface PlatformAdmin {
  email: string;
  mustChangePassword: boolean;
  hasMfa: boolean;
}

export interface PlatformStep {
  stage: PlatformStage;
  accessToken: string;
  expiresAt: string;
  admin: PlatformAdmin;
  recoveryCodes?: string[] | null;
}

export interface MfaEnrollment {
  secret: string;
  provisioningUri: string;
}

const AUTH = '/api/plataforma/auth';

/**
 * Sessão do painel da plataforma, separada da sessão de loja: outro token (só em memória) e outro cookie,
 * que só existe depois do código do app autenticador.
 */
@Injectable({ providedIn: 'root' })
export class PlatformAuthStore {
  private readonly http = inject(HttpClient);
  private readonly step = signal<PlatformStep | null>(null);
  private renewing: Promise<boolean> | null = null;

  readonly current = this.step.asReadonly();
  readonly accessToken = computed(() => this.step()?.accessToken ?? null);
  readonly signedIn = computed(() => this.step()?.stage === 'completo');

  async login(email: string, password: string): Promise<PlatformStep> {
    return this.store(await firstValueFrom(this.http.post<PlatformStep>(`${AUTH}/entrar`, { email, password })));
  }

  async verifyCode(code: string): Promise<PlatformStep> {
    return this.store(await firstValueFrom(this.http.post<PlatformStep>(`${AUTH}/codigo`, { code })));
  }

  async changePassword(currentPassword: string, newPassword: string): Promise<PlatformStep> {
    return this.store(await firstValueFrom(this.http.put<PlatformStep>(`${AUTH}/senha`, { currentPassword, newPassword })));
  }

  startEnrollment(): Promise<MfaEnrollment> {
    return firstValueFrom(this.http.post<MfaEnrollment>(`${AUTH}/mfa`, null));
  }

  async confirmEnrollment(code: string): Promise<PlatformStep> {
    return this.store(await firstValueFrom(this.http.post<PlatformStep>(`${AUTH}/mfa/confirmar`, { code })));
  }

  async logout(): Promise<void> {
    try {
      await firstValueFrom(this.http.post(`${AUTH}/sair`, null));
    } finally {
      this.step.set(null);
    }
  }

  /** Novo token de acesso pelo cookie da sessão (até 8 h depois do login). Uma renovação por vez. */
  renew(): Promise<boolean> {
    this.renewing ??= firstValueFrom(this.http.post<PlatformStep>(`${AUTH}/renovar`, null))
      .then((step) => {
        this.store(step);
        return true;
      })
      .catch(() => {
        this.step.set(null);
        return false;
      })
      .finally(() => (this.renewing = null));
    return this.renewing;
  }

  /** Volta ao início do login (ex.: o token da etapa venceu). */
  reset(): void {
    this.step.set(null);
  }

  private store(step: PlatformStep): PlatformStep {
    // Os códigos de recuperação aparecem uma vez na tela; não ficam guardados na sessão.
    this.step.set({ ...step, recoveryCodes: null });
    return step;
  }
}

/** Rotas que usam só o cookie (sem token de etapa). */
const cookieOnly = (url: string) => [`${AUTH}/entrar`, `${AUTH}/renovar`, `${AUTH}/sair`].includes(url);

/** Token da plataforma nas rotas /api/plataforma; fora do login, um 401 renova pelo cookie e repete uma vez. */
export const platformInterceptor: HttpInterceptorFn = (req, next) => {
  if (!req.url.startsWith('/api/plataforma/') || cookieOnly(req.url)) return next(req);

  const auth = inject(PlatformAuthStore);
  const withToken = (request: HttpRequest<unknown>) => {
    const token = auth.accessToken();
    return token ? request.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : request;
  };

  return next(withToken(req)).pipe(
    catchError((error: unknown) => {
      if (!(error instanceof HttpErrorResponse) || error.status !== 401 || req.url.startsWith(`${AUTH}/`)) return throwError(() => error);
      return from(auth.renew()).pipe(switchMap((renewed) => (renewed ? next(withToken(req)) : throwError(() => error))));
    }),
  );
};

/** Painel da plataforma só com sessão completa; ao recarregar a página, tenta retomar pelo cookie. */
export const platformGuard: CanActivateFn = async () => {
  const auth = inject(PlatformAuthStore);
  if (auth.signedIn() || (await auth.renew())) return true;
  return inject(Router).parseUrl('/plataforma/entrar');
};
