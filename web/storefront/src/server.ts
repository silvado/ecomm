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
// Atrás do Caddy (rede interna do Docker): esquema e IP do comprador vêm de X-Forwarded-*, só de proxies privados.
app.set('trust proxy', 'loopback, linklocal, uniquelocal');

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
        'X-Forwarded-For': req.ip ?? '',
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

/**
 * Robôs podem indexar a loja; a API fica de fora, exceto as fotos. A busca continua liberada enquanto não houver
 * sitemap.xml: é por ela (e pela página inicial) que os robôs chegam às peças.
 */
app.get('/robots.txt', (_req, res) => {
  res.type('text/plain').set('Cache-Control', 'public, max-age=3600');
  res.send(['User-agent: *', 'Allow: /', 'Disallow: /api/', 'Allow: /api/loja/fotos/', ''].join('\n'));
});

app.use(
  express.static(browserDistFolder, {
    maxAge: '1y',
    index: false,
    redirect: false,
  }),
);

app.use(async (req, res, next) => {
  try {
    const host = req.headers.host ?? '';
    const context: StoreRequestContext = {
      store: await loadStore(req),
      origin: `${req.protocol}://${host}`,
      api: { url: apiUrl, host, proto: req.protocol, clientIp: req.ip ?? '' },
    };
    const response = await angularApp.handle(req, context);
    if (!response) return next();
    // Curto: mudança de tema/textos aparece em até 1 min (RF01 CA3); erro e 404 nunca ficam em cache.
    res.setHeader('Cache-Control', context.store.kind === 'open' && response.status < 400 ? 'public, max-age=30' : 'no-store');
    res.setHeader('Vary', 'Host');
    await writeResponseToNodeResponse(response, res);
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
