# ADR-0012 — Resumo diário por e-mail

- **Status:** Aceito
- **Data:** 2026-10-18
- **Requisitos:** RF34 (CA1, CA2), RF35, RF31, RF28
- **Relacionados:** [ADR-0003](0003-provedor-de-ia.md) (IA), [ADR-0011](0011-respostas-a-compradores.md) (perguntas)

## Contexto

O RF34 pede um resumo diário, num horário configurável (padrão 07:00, horário de Brasília), com:
- as vendas de ontem por canal;
- as peças sem venda há mais de 90 dias;
- as perguntas pendentes.

Os números vêm de consultas; a IA só redige o texto (CA2). Ainda não há WhatsApp (Q12), então o canal é o e-mail, pelo mesmo envio da loja (Q38).

## Decisões

### 1. Quem recebe e quando

- **Destinatários:** os Donos da loja (perfil Dono no banco da plataforma).
- **Configuração:** em `store_settings` (`daily_summary_enabled`, `daily_summary_hour`).
- **HIPÓTESE:** ligado por padrão, às 7h, também nas lojas que já existiam.
- **Agendador:** `DailySummaryScheduler` (Worker) roda a cada 15 minutos. Para cada loja publicada e não suspensa (Active, ReadOnly), publica `PrepareDailySummary` com os e-mails dos Donos. Assim, o handler usa só o banco da loja.
- **Horário:** o handler confere a hora local. O resumo sai quando a hora atual passa da configurada; se o Worker estava parado no horário, sai assim que voltar, no mesmo dia.
- **Uma vez por dia:** `daily_summaries` (com RLS) tem índice único por loja e dia resumido. Duas rodadas cruzadas esbarram no índice, e na nova tentativa a segunda vê o resumo já enviado.

### 2. Números só por consultas (CA2)

`DailySummaryFactsQuery`:
- **Vendas de ontem por origem:** pedidos com `paid_at` no dia, em Brasília, fora das situações aguardando pagamento, cancelado e em análise.
- **Peças paradas:** ativas, com saldo, cadastradas há mais de 90 dias e sem baixa de venda nos últimos 90. A amostra traz as 5 paradas há mais tempo.
- **Perguntas sem resposta**, com a data da mais antiga.

A mesma consulta alimenta a prévia do painel.

### 3. IA só na abertura, conferida

A IA (`AiFeature.DailySummary`, sem limite de plano, auditada) recebe os números prontos e devolve `{texto}`: 2 a 4 frases, sem contas nem comparações.

`DailySummaryRules.AcceptAiText` descarta o texto quando:
- passa de 600 caracteres; ou
- cita qualquer número que não esteja nos fatos (contagens, totais, dias, data). Assim, ticket médio, porcentagem e crescimento inventado caem.

Os números sempre vão em tabela, das consultas. Se a IA estiver fora do ar, o resumo sai só com eles.

### 4. Envio

Um `SendDailySummaryEmail` por Dono, pela fila. Falha do servidor de e-mail é tentada de novo (RF28) e aparece em Integrações como "resumo diário".

O e-mail tem um botão "Abrir o painel" quando `Platform:PanelUrl` está configurado (`PANEL_URL` no `.env`).

## Consequências

- **Custo:** uma chamada de IA por loja por dia, curta.
- **Pendente:**
  - WhatsApp (Q12);
  - escolher quem recebe (hoje, todos os Donos);
  - comparar com dias anteriores (sempre por consulta, nunca pela IA);
  - ~~guardião da sincronização (RF33)~~: feito no [ADR-0013](0013-guardiao-da-sincronizacao.md), com o mesmo agendamento e envio.
