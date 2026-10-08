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
