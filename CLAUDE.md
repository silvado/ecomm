# CLAUDE.md

Guia para o Claude Code (e para pessoas) trabalharem neste repositório. Fonte da verdade do produto: [docs/BRIEF.md](docs/BRIEF.md). Requisitos detalhados: [docs/requisitos.md](docs/requisitos.md). Decisões: [docs/adr/](docs/adr/).

## Visão do produto

SaaS de e-commerce **multi-tenant** por assinatura para lojas de **autopeças (principalmente usadas)**. Cada loja (tenant) recebe loja virtual com domínio próprio e tema configurável, painel administrativo, **estoque único** sincronizado com Mercado Livre, Instagram (catálogo Meta) e OLX, e automações de IA (cadastro por foto, atendimento, preço sugerido, alertas, resumo diário).

**Um único código para todos os clientes.** Nada é escrito especificamente para o cliente piloto: diferenças entre clientes são configuração, plano (feature flags) ou dados.

## Stack

| Camada | Tecnologia |
|---|---|
| Back-end | .NET 10, ASP.NET Core Web API, EF Core (Npgsql), Clean Architecture |
| Mensageria/jobs | Wolverine 6: outbox transacional + filas duráveis no PostgreSQL (ver [ADR-0002](docs/adr/0002-fila-e-outbox.md)) |
| Front-end | Angular (última estável): `storefront` (com SSR, tema por tenant) e `admin` |
| Banco | PostgreSQL 17, isolamento por `tenant_id` + Row-Level Security |
| Arquivos | Storage compatível com S3 (MinIO em dev) |
| Proxy/SSL | Caddy com on-demand TLS restrito por endpoint `ask` |
| IA | Anthropic Claude (visão) atrás de `IAiProvider` |
| Infra | Docker Compose (dev e VPS), GitHub Actions |

## Estrutura (alvo)

```
src/
  Api/              ASP.NET Core: endpoints, auth, resolução de tenant, webhooks
  Worker/           Processamento de outbox/fila, jobs agendados (reconciliação, resumo)
  Application/      Casos de uso, portas (interfaces), DTOs, validação
  Domain/           Entidades, value objects, regras de negócio — sem dependências externas
  Infrastructure/   EF Core, RLS, cofre, adapters (canais, pagamento, fiscal, frete, IA, storage)
web/
  storefront/       Angular SSR — loja do tenant
  admin/            Angular — painel do lojista e superadmin
tests/
  Domain.Tests/  Application.Tests/  Integration.Tests/ (Testcontainers)  Contract.Tests/
deploy/             docker-compose, Caddyfile, scripts de backup
docs/               BRIEF, requisitos, arquitetura, modelo de dados, ADRs, backlog
```

Dependências apontam para dentro: `Api/Worker → Infrastructure → Application → Domain`. Domain não referencia EF Core nem nada de infraestrutura.

## Comandos

Pré-requisitos: .NET SDK 10, Docker em execução (Testcontainers), Node ≥ 24.15 (Angular 22).

```bash
dotnet build                                   # TreatWarningsAsErrors ativo
dotnet test                                    # Integration.Tests sobe PostgreSQL via Docker
dotnet test tests/Domain.Tests                 # só unitários, sem Docker
dotnet ef migrations add <Nome> --context TenantDbContext --project src/Infrastructure --startup-project src/Infrastructure --output-dir Persistence/Migrations    # banco lojas (RLS)
dotnet ef migrations add <Nome> --context PlatformDbContext --project src/Infrastructure --startup-project src/Infrastructure --output-dir Platform/Migrations    # banco plataforma
cp deploy/.env.example deploy/.env             # e troque as senhas
docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d
node tools/backlog/generate.mjs                # regenera docs/backlog.md e .csv
node tools/backlog/azure-sync.mjs --dry-run    # sincroniza com Azure Boards (requer AZURE_DEVOPS_EXT_PAT)
```

- Nova tabela de tenant: na migração, chamar `migrationBuilder.Sql(RowLevelSecurity.EnableFor("<tabela>"))`. O teste `Toda_tabela_do_schema_public_tem_RLS_habilitado_e_forcado` falha se esquecer.
- `nuget.config` do repositório usa só nuget.org (isola de feeds corporativos da máquina).

## Regras inegociáveis

1. **Isolamento de tenant (RNF01).** Nenhuma consulta pode retornar dados de outro tenant.
   - Toda tabela de negócio tem `tenant_id NOT NULL` e política RLS com `FORCE ROW LEVEL SECURITY`.
   - A aplicação conecta com papel **sem** `BYPASSRLS` e que **não é dono** das tabelas; migrações usam outro papel.
   - O tenant é fixado na abertura de cada conexão por interceptor (`set_config('app.tenant_id', ...)`) e limpo pelo reset do pool do Npgsql (`DISCARD ALL`) — nunca usar `No Reset On Close=true`. Sem tenant definido, o RLS não retorna nada (falha fechada). Ver [ADR-0001](docs/adr/0001-isolamento-multi-tenant.md).
   - EF Core aplica filtro global por `TenantId` **além** do RLS (defesa em profundidade). `IgnoreQueryFilters()` é proibido fora de código de superadmin revisado.
   - Todo novo agregado com dados de tenant ganha teste de isolamento em `Integration.Tests`.
2. **Sem venda dupla (RNF02).** Baixa e reserva de estoque são **atômicas no banco** (`UPDATE ... SET disponivel = disponivel - @n WHERE id = @id AND disponivel >= @n`, verificando linhas afetadas). Nunca "ler, checar em memória e gravar". Qualquer mudança nesse caminho exige teste de concorrência.
3. **Segredos (RNF05/RF06).** Nada de segredo no Git, em `appsettings.json`, em log ou em mensagem de exceção. Segredos de tenant ficam no cofre (criptografia envelope); segredos da plataforma em variáveis de ambiente. Logs usam redação de campos sensíveis.
4. **IA não age sozinha (RF35).** A IA nunca altera preço, estoque ou status de pedido. Toda chamada de IA é registrada (entrada resumida, saída, modelo, custo, usuário/tenant).
5. **Mensageria (ADR-0002):** eventos de tenant publicados com `DeliveryOptions { TenantId }`; handlers recebem `TenantDbContext`/`ITenantContext` por parâmetro; serviços scoped que dependem do tenant entram em `AlwaysUseServiceLocationFor` (`MessagingConfiguration`).
6. **Integrações externas atrás de interfaces** (`IChannelConnector`, `IPaymentGateway`, `IFiscalProvider`, `IShippingProvider`, `IAiProvider`, `IFileStorage`), sempre com implementação *fake* para dev e testes.
7. **Webhooks**: responder 2xx imediatamente, persistir o evento com chave de idempotência e processar pela fila.
8. **Não inventar APIs externas**: consultar a documentação oficial e citar a fonte no ADR ou em comentário no adapter.

## Convenções

- **Idioma (RNF10):** identificadores de código em inglês (`Part`, `StockReservation`); textos de negócio, mensagens ao usuário, documentação e commits em português do Brasil.
- Nomes de tabela/coluna em `snake_case` (Npgsql naming convention).
- Ids: `uuid` v7 gerados na aplicação.
- Dinheiro: `decimal(12,2)` + moeda; nunca `float`/`double`.
- Datas: `timestamptz`, sempre UTC; conversão para `America/Sao_Paulo` só na apresentação.
- Validação na Application (FluentValidation); invariantes no Domain.
- Logs estruturados (Serilog) com `tenant_id` e `correlation_id` em todo log.
- Testes: unitários no domínio; integração com PostgreSQL real (Testcontainers); contratos dos adapters contra mocks HTTP dos canais.
- Commits pequenos, em português, no imperativo (`Adiciona reserva atômica de estoque`).

## Forma de trabalho

- Propor plano e aguardar "ok" antes de criar/alterar muitos arquivos.
- Parar e perguntar em decisões difíceis de desfazer: modelo de dados, isolamento, fila, contrato de API pública.
- Faltou informação de negócio: registrar em `docs/requisitos.md` → "Em aberto" e seguir com a hipótese mais conservadora, sinalizada com `HIPÓTESE`.
- Ao fim de cada etapa: rodar testes, resumir o que mudou, propor a próxima.
- Backlog oficial no Azure Boards (`dev.azure.com/silvado/Ecomm`, processo Scrum; ids em `tools/backlog/azure-ids.Ecomm.json`); `docs/backlog.md` é o espelho legível.
