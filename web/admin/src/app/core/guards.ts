import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthStore, Permission, Session } from './auth';

/** Para onde mandar o usuário depois do login ou de uma mudança de sessão. */
export function landingFor(session: Session): string {
  if (session.mustChangePassword) return '/trocar-senha';
  if (!session.tenant) return '/escolher-loja';
  return '/';
}

export const authGuard: CanActivateFn = () =>
  inject(AuthStore).current() ? true : inject(Router).parseUrl('/entrar');

/** Senha provisória: nada além da troca de senha (o back-end também recusa). */
export const passwordChangedGuard: CanActivateFn = () =>
  inject(AuthStore).current()?.mustChangePassword ? inject(Router).parseUrl('/trocar-senha') : true;

export const tenantGuard: CanActivateFn = () =>
  inject(AuthStore).tenant() ? true : inject(Router).parseUrl('/escolher-loja');

/** Esconde telas que o perfil não pode usar. A API confere de novo — isto é só navegação. */
export const permissionGuard =
  (permission: Permission): CanActivateFn =>
  () =>
    inject(AuthStore).can(permission) ? true : inject(Router).parseUrl('/');
