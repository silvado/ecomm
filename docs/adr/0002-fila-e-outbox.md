# ADR-0002 — Fila, outbox e jobs

- **Status:** Aceito
- **Data:** 2026-10-07
- **Requisitos:** RF21 (webhooks via fila), RF24, RF25–RF28, RNF03, RNF06, RNF08

## Contexto

Precisamos de: outbox transacional (evento gravado na mesma transação da alteração de estoque), processamento assíncrono de webhooks, retentativas com backoff, fila de falhas (dead letter), mensagens agendadas (expiração de reserva, reconciliação horária, resumo diário) e métricas. Deve rodar bem numa única VPS de 8 GB e permitir escalar depois. Meta de propagação: ≤ 60 s.

## Alternativas consideradas

| Opção | Prós | Contras |
|---|---|---|
| A. RabbitMQ + MassTransit | Padrão de mercado, outbox EF pronto, escala horizontal | +1 serviço na VPS (memória, backup, monitoramento). **MassTransit v9 passou a ser comercial**; v8 segue open source com suporte anunciado até o fim de 2026 — risco de licença/custo |
| B. Hangfire (PostgreSQL storage) + outbox próprio | Painel pronto, jobs recorrentes | Hangfire é job scheduler, não mensageria; outbox, idempotência e roteamento ficam por nossa conta; LGPL |
| C. Outbox próprio + worker com `FOR UPDATE SKIP LOCKED` | Zero dependências, total controle | Reimplementar retentativa, agendamento, DLQ, métricas — muito código de infraestrutura para manter |
| **D. Wolverine (MIT) com persistência e transporte PostgreSQL** | Outbox/inbox transacional integrado ao EF Core, filas duráveis no próprio PostgreSQL, retentativa com backoff, mensagens agendadas, DLQ; suporta banco por tenant; troca para RabbitMQ depois só por configuração | Framework com convenções próprias (curva de aprendizado); transporte PG usa *polling* (latência de segundos, aceitável para 60 s); projeto mantido por uma empresa pequena |

## Decisão

**Opção D — Wolverine 6.x** (`WolverineFx`, `WolverineFx.EntityFrameworkCore`, `WolverineFx.Postgresql`):

- Persistência de mensagens no PostgreSQL (schema `wolverine`, fora do RLS, acessível pelo `app_user`).
- Alterações de estoque publicam `StockChanged` pelo outbox na mesma transação do EF Core.
- Webhooks: o endpoint grava `ExternalEvent` (chave única — RF26) e envia comando para a fila durável; responde 2xx em seguida.
- Política de erro: retentativa com backoff (10 s, 30 s, 2 min, 10 min, 30 min) e depois dead letter + alerta (RF28).
- Mensagens agendadas para expiração de reserva; jobs recorrentes (reconciliação, resumo diário) via agendamento do Wolverine ou `BackgroundService` simples com lock no banco.
- A aplicação depende de uma porta própria (`IEventBus`/handlers da Application) sempre que possível, para limitar o acoplamento ao framework.

**Plano de escala:** quando a VPS não bastar, trocar o transporte para RabbitMQ (`WolverineFx.RabbitMQ`) mantendo outbox no PostgreSQL; separar o Worker em outra máquina.

## Consequências

- Uma dependência a menos na VPS (sem broker).
- Métricas de fila (pendentes, idade, falhas) expostas via OpenTelemetry do Wolverine + consultas às tabelas.
- Se o Wolverine se mostrar inadequado no spike, recuar para a opção C (o desenho de outbox/handlers é o mesmo).
- **Spike obrigatório** na primeira tarefa da E1: Wolverine + EF Core + RLS + interceptor de tenant, medindo latência do outbox.

## Fontes

- Wolverine — PostgreSQL: https://wolverinefx.net/guide/durability/postgresql.html
- Wolverine — repositório e licença MIT: https://github.com/JasperFx/wolverine
- MassTransit — licença comercial a partir da v9: https://www.nuget.org/packages/MassTransit/9.1.2 e https://masstransit.io/support/upgrade

## Resultado do spike (2026-10-08)

PBI "Spike: Wolverine + EF Core + RLS". Testes em `tests/Integration.Tests/Messaging/OutboxSpikeTests.cs` (PostgreSQL real, papéis de produção). **Decisão confirmada**, com os ajustes abaixo.

| Pergunta | Resultado |
|---|---|
| Outbox transacional com EF Core? | Sim. `IDbContextOutbox<TenantDbContext>` + `SaveChangesAndFlushMessagesAsync`; transação não confirmada não entrega a mensagem. |
| Tenant chega ao handler? | Sim, via `DeliveryOptions.TenantId` → `Envelope.TenantId` → `TenantMessageMiddleware`. |
| RLS vale dentro do handler? | Sim, após os ajustes 1–3. Handler de B não enxerga dados de A; mensagem sem tenant não enxerga nada. |
| Gravação no handler persiste? | Sim (`AutoApplyTransactions` + transação do EF). |
| Latência commit → handler (local, 50 msgs) | **p50 ≈ 6 ms, p95 ≈ 8 ms, máx ≈ 13 ms** — muito abaixo da meta de 60 s (RNF03). Filas locais duráveis não esperam polling. |
| Roda sem DDL na aplicação? | Sim. Tabelas do Wolverine criadas pelo `app_migrator` (`AutoCreate.CreateOrUpdate` só no host de migração); a aplicação usa `AutoCreate.None` + `GRANT`s em `MessagingConfiguration.GrantPrivileges`. |

### Problemas encontrados e ajustes

1. **Wolverine 6 não traz mais o compilador em tempo de execução.** Adicionado `WolverineFx.RuntimeCompilation`. Para produção, avaliar código pré-gerado (`codegen write` + `TypeLoadMode.Static`) — tarefa no PBI de implantação.
2. **Registros por fábrica lambda são recusados** (`ServiceLocationPolicy.NotAllowed`). `ITenantContext` passou a ser `TenantScopeAccessor`, registrado por tipo.
3. **O código gerado criava instâncias próprias dos serviços de tenant**, e o DbContext do handler via tenant `null` enquanto o middleware via o tenant certo (falha fechada: nenhuma linha — seguro, mas inútil). Correção: `AlwaysUseServiceLocationFor<TenantScope | ITenantContext | TenantDbContext>()`, que resolve tudo do escopo de DI da mensagem. A classe concreta foi renomeada de `TenantContext` para `TenantScope` porque o gerador dava o mesmo nome de variável a `ITenantContext` e `TenantContext`.
4. **Defesa adicional:** o interceptor de RLS reaplica `app.tenant_id` antes de cada comando se o tenant mudou depois da abertura da conexão; o DbContext cria o próprio interceptor com o `ITenantContext` que recebe (opções do DbContext agora singleton, como o Wolverine recomenda). Coberto por `Tenant_definido_depois_de_abrir_a_conexao_vale_para_o_proximo_comando`.

### Regras que ficam

- Handler que toca dados de tenant recebe `TenantDbContext`/`ITenantContext` por parâmetro; nunca cria escopo próprio.
- Toda publicação de evento de tenant usa `DeliveryOptions { TenantId = ... }` (ou herda do envelope em mensagens em cascata).
- Novos serviços scoped que dependam do tenant devem entrar em `AlwaysUseServiceLocationFor`.

## Política de erro implementada (2026-10-12, RF28/RF29)

Conferido no Wolverine 6.48: [Error Handling](https://wolverinefx.net/guide/handlers/error-handling.html), [Dead Letter Storage](https://wolverinefx.net/guide/durability/dead-letter-storage.html) e a API pública da DLL (`IEnvelopeLifecycle.MoveToDeadLetterQueueAsync`, `IWolverineRuntime.Storage.DeadLetters`, `MarkDeadLetterEnvelopesAsReplayableAsync`, `WithBoundedJitter`).

- **Transitórias** (`HttpRequestException`, `TimeoutException`, `PaymentGatewayException`, `ShippingProviderException`, `NpgsqlException.IsTransient`): `ScheduleRetry(10 s, 30 s, 2 min, 10 min, 30 min).WithBoundedJitter(0.2)` e depois a ação final. **Qualquer outra exceção** vai direto para a ação final. Intervalos configuráveis em `Messaging:RetryDelaysSeconds` (os testes usam 0,2 s).
- **Ação final** (`MessageFailures.RecordAndDeadLetterAsync`): grava `message_failures` (tabela da loja, com RLS, motivo redigido e o id da mensagem), registra log crítico (alerta da plataforma) e move a mensagem para `wolverine.wolverine_dead_letters`. O id da falha é o mesmo do envelope, o que permite **reprocessar** pelo painel (`replayable = true`, lido pelo agente de durabilidade).
- **Só vale para mensagens da fila.** Em `InvokeAsync`, o Wolverine aplica sozinho apenas `Retry`/`RetryWithCooldown`; agendar e a ação final não rodam, e o erro volta para quem chamou. É o comportamento que a API espera, por exemplo para a chave única da venda de balcão.
- **Correlação:** a requisição HTTP define `IMessageContext.CorrelationId`, e as mensagens em cascata herdam esse valor. O middleware de tenant copia a correlação do envelope para o contexto de diagnóstico (logs e log de integração).
- **Métricas da fila:** `GET /internal/metricas`, só na rede interna. Mostra pendentes, agendadas e falhas a partir das tabelas do Wolverine. A tabela de entrada não guarda a chegada, então a idade só existe para agendadas e falhas. A métrica de idade p95 da RNF03 fica para quando houver OpenTelemetry/Prometheus na VPS.
