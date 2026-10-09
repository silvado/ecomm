import { RenderMode, ServerRoute } from '@angular/ssr';

/** Toda página depende da loja do Host: renderização por requisição, nunca pré-renderizada (ADR-0006). */
export const serverRoutes: ServerRoute[] = [
  {
    path: '**',
    renderMode: RenderMode.Server,
  },
];
