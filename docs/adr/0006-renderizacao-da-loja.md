# ADR-0006 — Renderização da loja (SSR)

- **Status:** Aceito
- **Data:** 2026-10-07
- **Requisitos:** RF13, RF04, RF22

## Contexto

A loja precisa ser encontrada no Google e compartilhada em redes sociais (preview com foto e preço). Uma SPA Angular sem renderização no servidor entrega HTML vazio a robôs e previews.

## Alternativas

| Opção | Prós | Contras |
|---|---|---|
| A. SPA pura | Simples de hospedar | SEO e previews ruins |
| **B. Angular SSR (`@angular/ssr`) com hidratação** | SEO, previews, mesmo stack Angular | Processo Node no servidor (memória na VPS) |
| C. Pré-renderização estática por tenant | Rápido | Inviável com estoque mudando a todo momento e muitos tenants |

## Decisão

**Opção B.** O servidor SSR recebe o `Host` original (repassado pelo Caddy), chama a API com esse Host e renderiza tema + conteúdo do tenant. Cache HTTP curto (≤ 60 s) para páginas de listagem; página de produto sem cache de estoque no cliente (preço/disponibilidade revalidados na hidratação). `admin` continua SPA pura.

## Consequências

- Um container Node a mais (~150–300 MB).
- Tema por tenant via CSS custom properties injetadas no SSR — sem build por cliente.

## Atualização (2026-10-10): vitrine implementada (RF13)

- O `server.ts` resolve a loja pelo Host antes de renderizar (`/api/loja/identidade`) e entrega ao Angular por `REQUEST_CONTEXT`: loja, origem pública e como chamar a API interna. `allowedHosts: ['*']` justificado no código (o Caddy só emite certificado para domínios aprovados pelo `ask`; o servidor nunca monta URL a partir do Host).
- No SSR, as chamadas `/api/loja/*` do Angular vão direto para `API_URL` com `X-Forwarded-Host/Proto/For` (`apiInterceptor`); o cache de transferência do Angular leva as respostas para a hidratação, sem segunda chamada. Express com `trust proxy` só para redes privadas (esquema e IP reais vindos do Caddy).
- Dados das páginas por resolvers: peça inexistente/inativa → **404** real; título desatualizado na URL → **301** para `/peca/{id}-{titulo}` atual. Páginas de erro e 404 com `no-store`; as demais com `public, max-age=30`.
- SEO: `<title>`, description, canonical, Open Graph (imagem absoluta de 1600 px) e JSON-LD `Product`/`Offer` (BRL, disponibilidade, estado) com `<` escapado. `robots.txt` libera tudo menos a API (exceto fotos). Pendência: `sitemap.xml` (sem ele, robôs chegam às peças pela página inicial e pela busca).
- Lighthouse mobile local (2026-10-10): desempenho 89–92, SEO/acessibilidade/boas práticas 100 nas páginas de início, busca e peça.