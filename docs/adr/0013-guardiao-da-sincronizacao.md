# ADR-0013 — Guardião da sincronização

- **Status:** Aceito
- **Data:** 2026-10-18
- **Requisitos:** RF33 (CA1, CA2), RF28, RF29, RF21 CA2, RF35
- **Relacionados:** [ADR-0003](0003-provedor-de-ia.md) (IA), [ADR-0012](0012-resumo-diario.md) (resumo diário: mesmo agendamento e envio)

## Contexto

O RF33 pede que a plataforma:
- analise as falhas recentes de integração;
- explique cada uma em linguagem simples, com uma ação sugerida;
- avise por e-mail. WhatsApp fica para quando houver provedor (Q12).

Até aqui, o alerta era só a faixa no painel e o log crítico (Q36).

## Decisões

### 1. Problemas só por consultas (CA1)

`SyncGuardianQuery` lê o banco da loja e encontra três tipos de problema:

| Tipo | Regra | Chave |
|---|---|---|
| Canal sem autorização | conexão em `ReauthRequired` (RF21 CA2) | `acesso:{canal}` |
| Falha em segundo plano | `message_failures` pendentes das últimas 24 h, agrupadas pela descrição (RF28) | `falha:{descrição}` |
| Chamada que segue falhando | mesma chamada (canal + operação) com 3 ou mais falhas nas últimas 2 h e sem acerto depois da última (RF29) | `chamada:{canal}:{operação}` |

- Uma falha isolada não conta: a política de novas tentativas já cuida dela.
- A falha do próprio e-mail de alerta fica de fora, para o guardião não avisar que o aviso falhou.

### 2. Ação sugerida por regra; a IA só explica

`SyncGuardianRules.Classify` escolhe a ação a partir do tipo e do texto do erro, já redigido:

| Ação | Quando |
|---|---|
| Reconectar | canal sem autorização, ou erro 401/403 ou de token |
| Corrigir a peça | trabalho em anúncio ou catálogo com erro de dado (400/409/422, obrigatório, inválido) |
| Reprocessar | demais falhas em segundo plano |
| Aguardar o serviço | erro 5xx, tempo esgotado ou conexão |
| Suporte | o resto |

A IA (`AiFeature.SyncDiagnosis`, auditada e sem limite de plano) recebe os problemas numerados e devolve `{diagnosticos: [{numero, texto}]}`.

`AcceptExplanation` recusa o texto quando:
- passa de 300 caracteres;
- traz link ou e-mail;
- cita número que não seja a quantidade, um número do erro ou a janela (2 h, 24 h).

Texto recusado, ou IA fora do ar, dá lugar a uma explicação fixa. A IA nunca escolhe a ação nem age (RF35).

### 3. Quando avisa (CA2)

- **Agendador:** `SyncGuardianScheduler` (Worker) roda a cada 15 minutos. Para cada loja publicada e não suspensa, publica `CheckSyncHealth` com os e-mails dos Donos. Usa a mesma consulta de Donos do resumo diário (`StoreOwners`).
- **Sem repetir:** `sync_alerts` (com RLS) guarda as chaves avisadas. Um problema só volta a ser avisado depois de 24 horas, se continuar; um problema novo sai sozinho.
- **Concorrência:** duas rodadas ao mesmo tempo se enfileiram num `pg_advisory_xact_lock` por loja, dentro da transação do handler. A segunda vê o alerta da primeira.
- **Configuração:** em `store_settings.sync_alerts_enabled`. **HIPÓTESE:** ligado por padrão, também nas lojas existentes. Só o Dono desliga (Integrações).

### 4. Envio e painel

- Um `SendSyncAlertEmail` por Dono, pela fila, com novas tentativas (RF28). Aparece em Integrações como "alerta da sincronização".
- O e-mail traz, para cada problema:
  - o título;
  - a explicação;
  - **O que fazer**;
  - o horário e o erro.

  Traz também o botão "Abrir Integrações" (`Platform:PanelUrl`).
- `GET /api/painel/integracoes/diagnostico` (Dono e Operador) mostra os problemas de agora, com a explicação fixa, sem chamar a IA.

## Consequências

- **Custo:** uma chamada de IA só quando há problema novo, no máximo uma por loja a cada 15 minutos.
- **Lembrete diário:** um canal sem autorização gera um lembrete por dia enquanto não for reconectado.
- **Pendente:**
  - WhatsApp (Q12);
  - aviso do certificado A1 por e-mail (RF06), que pode entrar como novo tipo de problema;
  - alertas da plataforma para a equipe (hoje, log crítico).
