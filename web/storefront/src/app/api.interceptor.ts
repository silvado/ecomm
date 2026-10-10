import { HttpInterceptorFn } from '@angular/common/http';
import { inject, REQUEST_CONTEXT } from '@angular/core';
import type { StoreRequestContext } from './store';

/**
 * No navegador, `/api/loja/*` vai para o próprio domínio da loja (o Caddy encaminha à API). No servidor (SSR), a
 * mesma chamada vai direto para a API interna — URL fixa vinda do ambiente, nunca do Host — levando o Host da loja
 * em X-Forwarded-Host para a API resolver o tenant (RF04). O cache de transferência do Angular usa a URL relativa,
 * então a hidratação reaproveita a resposta sem chamar a API de novo.
 */
export const apiInterceptor: HttpInterceptorFn = (req, next) => {
  const context = inject(REQUEST_CONTEXT, { optional: true }) as StoreRequestContext | null;
  if (!context?.api || !req.url.startsWith('/api/loja/')) return next(req);

  const { url, host, proto, clientIp } = context.api;
  return next(
    req.clone({
      url: url + req.url,
      setHeaders: { 'X-Forwarded-Host': host, 'X-Forwarded-Proto': proto, 'X-Forwarded-For': clientIp },
    }),
  );
};
