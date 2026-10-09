import {
  AngularNodeAppEngine,
  createNodeRequestHandler,
  isMainModule,
  writeResponseToNodeResponse,
} from '@angular/ssr/node';
import express from 'express';
import { join } from 'node:path';
import type { StoreRequestContext, StoreState } from './app/store';

const browserDistFolder = join(import.meta.dirname, '../browser');

/** API na rede interna (compose: http://api:8080). Nunca derivada do Host da requisição. */
const apiUrl = process.env['API_URL'] ?? 'http://localhost:8080';

/**
 * Cada loja tem o próprio domínio, então o Host não cabe numa lista fixa. `*` é o recomendado pelo time do Angular
 * quando o proxy já valida o Host (https://github.com/angular/angular-cli/issues/32910): o Caddy só emite
 * certificado para domínios aprovados pelo endpoint `ask` (ADR-0004), e este servidor não monta URLs a partir do Host
 * — a única chamada de saída vai para `apiUrl`, fixo.
 */
const angularApp = new AngularNodeAppEngine({ allowedHosts: ['*'] });

const app = express();

/** Resolve a loja pelo Host (RF04): a API decide, com o Host repassado em X-Forwarded-Host. */
async function loadStore(req: express.Request): Promise<StoreState> {
  const host = req.headers.host;
  if (!host) return { kind: 'not-found' };
  try {
    const response = await fetch(`${apiUrl}/api/loja/identidade`, {
      headers: {
        Accept: 'application/json',
        'X-Forwarded-Host': host,
        'X-Forwarded-Proto': req.protocol,
        'X-Forwarded-For': req.socket.remoteAddress ?? '',
      },
      signal: AbortSignal.timeout(3000),
    });
    if (response.ok) return { kind: 'open', store: await response.json() };
    if (response.status === 503) return { kind: 'unavailable' };
    if (response.status === 404) return { kind: 'not-found' };
    return { kind: 'error' };
  } catch {
    return { kind: 'error' };
  }
}

app.use(
  express.static(browserDistFolder, {
    maxAge: '1y',
    index: false,
    redirect: false,
  }),
);

app.use(async (req, res, next) => {
  try {
    const context: StoreRequestContext = { store: await loadStore(req) };
    // Curto: mudança de tema/textos aparece em até 1 min (RF01 CA3); erro nunca fica em cache.
    res.setHeader('Cache-Control', context.store.kind === 'open' ? 'public, max-age=30' : 'no-store');
    res.setHeader('Vary', 'Host');
    const response = await angularApp.handle(req, context);
    if (response) await writeResponseToNodeResponse(response, res);
    else next();
  } catch (error) {
    next(error);
  }
});

if (isMainModule(import.meta.url) || process.env['pm_id']) {
  const port = process.env['PORT'] || 4000;
  app.listen(port, (error) => {
    if (error) {
      throw error;
    }

    console.log(`Node Express server listening on http://localhost:${port}`);
  });
}

/** Request handler used by the Angular CLI (for dev-server and during build). */
export const reqHandler = createNodeRequestHandler(app);
