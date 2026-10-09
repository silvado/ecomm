# E-commerce de autopeças (SaaS multi-tenant)

Produto e regras: [docs/BRIEF.md](docs/BRIEF.md) · requisitos: [docs/requisitos.md](docs/requisitos.md) · decisões: [docs/adr/](docs/adr/) · guia de desenvolvimento e comandos: [CLAUDE.md](CLAUDE.md).

## Ambiente local

Pré-requisitos: .NET SDK 10, Docker em execução, Node ≥ 24.15.

```bash
cp deploy/.env.example deploy/.env      # troque as senhas e chaves
docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d --build
```

| Endereço | O quê |
|---|---|
| `https://demo.localhost` | loja demo (criada se `DEV_OWNER_EMAIL`/`DEV_OWNER_PASSWORD` estiverem no `.env`) |
| `https://<slug>.localhost` | lojas criadas com `migrator criar-loja` (ver CLAUDE.md) |
| `http://localhost:8080` | API |
| `http://localhost:4200` | painel do lojista: `cd web/admin && npx ng serve` (proxy de `/api` para a API) |

### Certificado HTTPS das lojas em dev

Em dev o Caddy emite os certificados com uma autoridade certificadora **local** (`local_certs` em `deploy/caddy/Caddyfile.dev`), criada no volume do Caddy da sua máquina. O navegador não a conhece e mostra erro de certificado — é o esperado; em produção os certificados vêm do Let's Encrypt (ADR-0004).

- **Rápido:** no aviso, *Avançado → Continuar* (vale por endereço).
- **Definitivo (Windows):** confiar na autoridade local do Caddy, uma vez:
  ```powershell
  docker cp ecommerce-caddy-1:/data/caddy/pki/authorities/local/root.crt "$env:USERPROFILE\caddy-local-root.crt"
  certutil -user -addstore Root "$env:USERPROFILE\caddy-local-root.crt"
  ```
  Confirme a janela do Windows e reabra o navegador. Chrome e Edge usam a lista do Windows; no Firefox, ative `security.enterprise_roots.enabled` em `about:config`. Para desfazer: `certutil -user -delstore Root "Caddy Local Authority - 2026 ECC Root"` (o ano no nome muda com a autoridade).
- Apagar o volume do Caddy (`caddydata`) gera uma autoridade nova: repita o passo acima.

A chave dessa autoridade nunca sai da sua máquina e só emite certificados para os endereços `.localhost` do ambiente de dev.
