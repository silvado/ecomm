import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, from, switchMap, throwError } from 'rxjs';
import { AuthStore } from './auth';

/** Rotas de sessão usam o cookie, não o token — exceto a troca de senha. */
const usesCookieOnly = (url: string) => url.startsWith('/api/auth/') && !url.endsWith('/senha');

/** Envia o token de acesso e, se ele expirou (401), renova pelo cookie e repete a requisição uma vez. */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  if (!req.url.startsWith('/api/') || usesCookieOnly(req.url)) return next(req);

  const auth = inject(AuthStore);
  const withToken = (request: HttpRequest<unknown>) => {
    const token = auth.accessToken();
    return token ? request.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : request;
  };

  return next(withToken(req)).pipe(
    catchError((error: unknown) => {
      if (!(error instanceof HttpErrorResponse) || error.status !== 401) return throwError(() => error);
      return from(auth.refresh()).pipe(switchMap((renewed) => (renewed ? next(withToken(req)) : throwError(() => error))));
    }),
  );
};
