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
