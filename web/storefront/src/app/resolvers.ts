import { HttpErrorResponse } from '@angular/common/http';
import { inject, RESPONSE_INIT } from '@angular/core';
import { RedirectCommand, ResolveFn, Router } from '@angular/router';
import { catchError, map, of } from 'rxjs';
import { CatalogApi, partIdFromSlug, partPath, SearchParams, StorePartPage, StorePartView } from './catalog';

/** Define o status HTTP da página (só existe no SSR): 404/503 de verdade para robôs, prévias e cache. */
function statusSetter(): (status: number, location?: string) => void {
  const response = inject(RESPONSE_INIT, { optional: true });
  return (status, location) => {
    if (!response) return;
    response.status = status;
    if (location) response.headers = { ...(response.headers as Record<string, string> | undefined), Location: location };
  };
}

const EMPTY_PAGE: StorePartPage = { items: [], total: 0, page: 1, pageSize: 24 };

export function searchParamsFrom(query: Record<string, string | undefined>): SearchParams {
  const number = (value?: string) => (value && /^\d{1,4}$/.test(value) ? Number(value) : undefined);
  return { q: query['q'], marca: query['marca'], modelo: query['modelo'], ano: number(query['ano']), pagina: number(query['pagina']) };
}

/** API fora do ar: página vazia com 503 (a página mostra a mensagem; nunca fica em cache). */
export const recentPartsResolver: ResolveFn<StorePartPage> = () => {
  const setStatus = statusSetter();
  return inject(CatalogApi).search({}, 12).pipe(catchError(() => (setStatus(503), of(EMPTY_PAGE))));
};

export const searchResolver: ResolveFn<StorePartPage> = (route) => {
  const setStatus = statusSetter();
  return inject(CatalogApi).search(searchParamsFrom(route.queryParams)).pipe(catchError(() => (setStatus(503), of(EMPTY_PAGE))));
};

/**
 * Página da peça: /peca/{id}-{titulo}. Quem manda é o id; se o título mudou, redireciona para o endereço atual
 * (um endereço só por peça). Peça inexistente, inativa ou de outra loja → null e 404.
 */
export const partResolver: ResolveFn<StorePartView | null> = (route) => {
  const router = inject(Router);
  const setStatus = statusSetter();
  const slug = route.paramMap.get('slug') ?? '';
  const id = partIdFromSlug(slug);
  if (!id) {
    setStatus(404);
    return null;
  }

  return inject(CatalogApi)
    .get(id)
    .pipe(
      map((part) => {
        const canonical = partPath(part);
        if (canonical === `/peca/${slug}`) return part;
        // No servidor: 301 de verdade (robôs e links antigos). No navegador: navegação para o endereço atual.
        setStatus(301, canonical);
        return new RedirectCommand(router.parseUrl(canonical), { replaceUrl: true });
      }),
      catchError((error: unknown) => {
        setStatus(error instanceof HttpErrorResponse && error.status === 404 ? 404 : 503);
        return of(null);
      }),
    );
};
