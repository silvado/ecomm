# ADR-0004 — Proxy, SSL e domínios próprios

- **Status:** Proposto
- **Data:** 2026-10-07
- **Requisitos:** RF02, RF03, RF04

## Contexto

Cada loja tem subdomínio da plataforma (`slug.plataforma.com.br`) e pode ter domínio próprio. Os certificados precisam ser automáticos e só para domínios cadastrados. Registros MX do cliente nunca são tocados.

## Alternativas consideradas

| Opção | Prós | Contras |
|---|---|---|
| **A. Caddy com on-demand TLS + `ask`** | Certificado no primeiro acesso, renovação automática, config mínima | Primeiro acesso a um domínio novo é mais lento (emissão no handshake) |
| B. Nginx + certbot por domínio | Conhecido | Script para cada novo domínio, reload, renovação; frágil |
| C. Cloudflare for SaaS (Custom Hostnames) | Sem limites de emissão, CDN, DDoS | Custo por hostname acima da cota; dependência externa; avaliar quando houver escala |

## Decisão

**Opção A**, com dois cuidados:

1. **Subdomínios da plataforma usam certificado curinga** `*.plataforma.com.br` (desafio DNS-01 via módulo do provedor DNS no Caddy). Motivo: o Let's Encrypt limita a **50 certificados novos por domínio registrado a cada 7 dias**; emitir um certificado por subdomínio estouraria o limite ao criar muitas lojas.
2. **Domínios próprios usam on-demand TLS** restrito pelo endpoint `ask`:
   - Caddy chama `GET http://api:8080/internal/tls/ask?domain=<host>`.
   - A API responde **200** se o domínio estiver cadastrado **e verificado** para um tenant ativo; **404** caso contrário. Qualquer resposta não-2xx impede a emissão.
   - Endpoint só acessível na rede interna do Docker.

**DNS do domínio próprio (RF03):**
- `www.dominiodocliente.com.br CNAME slug.plataforma.com.br` — recomendado; desacopla o cliente do nosso IP.
- Domínio raiz: CNAME não é permitido no ápice. Ordem de recomendação:
  1. ALIAS/ANAME/CNAME flattening, se o provedor DNS do cliente suportar;
  2. redirecionamento raiz → `www` oferecido pelo registrador;
  3. registro `A` para o IP da plataforma (exige IP estável — usar IP flutuante/reservado da VPS para poder trocar de servidor sem que clientes alterem DNS).
- Verificação: resolver o `www` (e o raiz, se configurado) e confirmar que aponta para a plataforma; nunca consultar ou alterar MX/TXT.

**Roteamento:** Caddy encaminha todo tráfego da loja para o `storefront` (SSR), que repassa o `Host` à API; `admin.plataforma.com.br` → app admin; `api.plataforma.com.br` → API (webhooks).

## Consequências

- Usar o ambiente *staging* do Let's Encrypt em dev/testes para não esbarrar nos limites.
- O armazenamento de certificados do Caddy (`/data`) precisa de volume persistente e entra no backup.
- Domínio raiz com registro A amarra o cliente ao IP — documentado no onboarding.

## Fontes

- Caddy — Automatic HTTPS / On-Demand TLS: https://caddyserver.com/docs/automatic-https#on-demand-tls
- Caddy — opção global `on_demand_tls` / `ask` (`?domain=`, 2xx autoriza): https://caddyserver.com/docs/caddyfile/options#on-demand-tls
- Let's Encrypt — Rate Limits (50 por domínio registrado / 7 dias): https://letsencrypt.org/docs/rate-limits/
