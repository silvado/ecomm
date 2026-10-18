# ADR-0011 — Respostas a compradores com IA (perguntas do Mercado Livre)

- **Status:** Aceito
- **Data:** 2026-10-18
- **Requisitos:** RF31 (CA1–CA4), RF35, RF26, RF28, RF39, RNF04
- **Relacionados:** [ADR-0003](0003-provedor-de-ia.md) (IA), [ADR-0009](0009-integracao-mercado-livre.md) (Mercado Livre)

## Contexto

O RF31 pede uma sugestão de resposta às perguntas dos compradores, feita só com o cadastro (CA1). A IA classifica cada pergunta:
- **simples:** resposta automática, se a loja ligar;
- **complexa:** rascunho para a loja revisar (CA2).

A resposta nunca pode afirmar uma compatibilidade que não está no cadastro, e isso é conferido por um conjunto de avaliação (CA3). A resposta automática vem desligada (CA4, HIPÓTESE).

As mensagens do Instagram dependem do App Review da Meta (Q08). Esta etapa cobre só as perguntas do Mercado Livre.

Fontes, consultadas em 18/10/2026:
- [Perguntas e respostas](https://developers.mercadolivre.com.br/pt_br/perguntas-e-respostas) (página atualizada em 05/06/2025):
  - consulta: `GET /questions/{id}` com `api_version=4`, que devolve `id`, `item_id`, `status`, `text`, `date_created` e `answer {text, status, date_created}`;
  - situações: `UNANSWERED`, `ANSWERED`, `BANNED`, `CLOSED_UNANSWERED`, `DELETED`, `DISABLED` e `UNDER_REVIEW`;
  - resposta: `POST /answers {question_id, text}`, que devolve a pergunta respondida.
- [Notificações](https://developers.mercadolivre.com.br/pt_br/produto-receba-notificacoes): tópico `questions`, recurso `/questions/{id}`. Os avisos perdidos também vêm por `missed_feeds`.

## Decisões

### 1. Aviso, consulta e registro

O aviso `questions` entra pelo inbox (RF26), como os demais. O processador do ML publica `ImportBuyerQuestion` e o handler consulta a pergunta na API, porque o aviso nunca é confiado.

**Pergunta nova sem resposta:** vira um registro em `buyer_questions` (RLS):
- ligada ao anúncio e, pelo `channel_listings`, à peça;
- única por conexão e id da pergunta;
- se o anúncio não é da plataforma, fica sem peça e sem rascunho, com uma nota.

**Pergunta já registrada:** acompanha o ML.
- `ANSWERED` vira respondida "no Mercado Livre".
- `DELETED`, `BANNED`, `CLOSED_UNANSWERED` e `DISABLED` viram encerrada.

O `missed_feeds` passa a recuperar também o tópico `questions`.

**Dados do comprador:** não são lidos (LGPD). Guardamos só o texto da pergunta. As perguntas entram na exportação completa da loja (`perguntas.csv`, RF39).

### 2. Rascunho da IA só com o cadastro (CA1)

`DraftBuyerAnswer` monta o pedido `AiFeature.BuyerAnswer`, sem limite de plano, registrado e auditado como toda chamada de IA (RF35).

**O que vai para a IA:**
- da peça: título, estado, disponibilidade (ativa e com saldo), código interno, códigos OEM, medidas, descrição e veículos compatíveis (marca, modelo, motor e anos, até 60);
- da loja: se oferece retirada no balcão (o endereço não vai) e a política de troca e devolução.

**O que não vai:** o preço. Perguntas de preço, desconto, pagamento e frete são da loja.

As instruções tratam o texto da pergunta como dado: instruções escritas pelo comprador não devem ser seguidas.

**Formato da resposta (structured outputs):**
- `tipo`: `simples` ou `complexa`;
- `fala_de_compatibilidade`;
- `resposta`: até 400 caracteres pedidos, com colchetes no que a loja precisa completar;
- `motivo`.

**Falhas:** IA fora do ar, recusa ou resposta fora do formato deixam uma nota ("responda à mão") e nunca travam a pergunta. A loja pode pedir um novo rascunho pelo painel.

### 3. Regras fixas por cima da classificação (CA2/CA3)

A IA classifica, mas quem decide é `BuyerAnswerRules`, na Application, sem dependências. A resposta só sai sozinha quando, ao mesmo tempo:
- a resposta automática está ligada;
- a IA disse `simples`;
- nem a IA nem a pergunta tratam de veículo:
  - a IA não marcou compatibilidade;
  - a pergunta não traz palavras como "serve", "encaixa", "compatível", "ano", "modelo", "motor", nem um ano de 1950 a 2049;
- a resposta não tem telefone (8 ou mais dígitos que não sejam um código da peça), e-mail, site, WhatsApp ou rede social, porque o ML proíbe contato fora dele;
- a resposta não fala de preço, desconto ou pagamento ("R$", "%", "desconto", "pix"…), porque a IA não mexe em preço (RF35);
- a resposta tem até 2.000 caracteres. **HIPÓTESE:** a documentação do ML não dá o limite.

**HIPÓTESE conservadora para o CA3:** toda pergunta sobre veículo vira rascunho, mesmo com o veículo no cadastro. A nota diz se a peça tem ou não veículos cadastrados. Errar a compatibilidade gera devolução e reclamação no ML; a revisão custa pouco.

**Rascunho pedido de novo no painel:** nunca sai sozinho, porque quem pediu revisa.

### 4. Conjunto de avaliação

`tests/Application.Tests/Channels/buyer-answer-eval.json` tem 18 casos, com perguntas inventadas e resposta da IA conhecida:
- respostas certas;
- respostas **erradas de propósito**: afirma compatibilidade e diz "simples", põe telefone ou e-mail, oferece desconto, obedece a uma instrução escondida na pergunta.

Para cada caso, o conjunto diz se a resposta pode sair sozinha. Roda na CI. Qualquer mudança nas regras, no prompt ou no modelo passa por ele.

**Pendente:** rodar as mesmas perguntas contra o modelo real, para medir a qualidade da classificação e do texto. Isso depende da chave da Anthropic. Mesmo um modelo que erre não consegue mandar sozinho uma resposta de compatibilidade, porque a trava é a regra fixa.

### 5. Envio

`SendBuyerAnswer` trava a linha da pergunta (`FOR UPDATE`) e chama `POST /answers`, com registro no log de integração. Assim, dois envios ao mesmo tempo (clique duplo, duas abas) resultam em um só envio.

| Situação | O que acontece |
|---|---|
| ML recusa (400) | O motivo vai para a pergunta e para a loja |
| ML fora do ar, envio pelo painel | Mensagem para tentar de novo |
| ML fora do ar, envio automático | A fila tenta de novo (RF28) |
| Token revogado | A pergunta fica com o aviso de autorizar de novo |

A resposta guarda a origem: painel (com o usuário), automática ou no ML.

### 6. Permissões e configuração

- **Perguntas:** permissão `SupportManage` (Dono e Operador), até então sem uso.
- **Resposta automática:** só o Dono liga (`StoreManage`). Fica em `store_settings.auto_answer_questions`, falso por padrão (CA4).

### 7. Simulação

**ML simulado:** guarda as perguntas e responde `GET /questions/{id}` (só com `api_version=4`) e `POST /answers`. A página de dev `/api/dev/mercado-livre/vendas` ganhou o formulário "Perguntar no anúncio".

**IA simulada:** responde que a peça está disponível. Às perguntas com "serve", responde errado de propósito ("Serve sim!", simples), para mostrar a trava em dev e nos testes.

## Consequências

- **Teste real:** exige o app do ML no DevCenter, com o tópico `questions` assinado, e a chave da Anthropic.
- **Pendente:**
  - mensagens do Instagram (após o App Review — Q08);
  - mensagens pós-venda do ML;
  - avaliação contra o modelo real;
  - perguntas pendentes no resumo diário (RF34).
- **Custo:** uma chamada de IA por pergunta nova, mais as dos novos rascunhos pedidos no painel. Contadas no uso do mês, sem limite de plano (decisão de 16/10/2026).
