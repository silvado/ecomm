# ADR-0009 — Integração com o Mercado Livre: conexão da conta (OAuth)

- **Status:** Aceito (parte 1: conexão; parte 2: notificações; publicação e pedidos nos próximos PRs)
- **Data:** 2026-10-16
- **Requisitos:** RF21 (CA1, CA2, CA5), RF06, RF26, RF37, RNF05

## Contexto

O RF21 pede um único app da plataforma no DevCenter do Mercado Livre, autorizado por cada loja via OAuth 2.0, com os tokens no cofre e renovação automática. Publicar anúncios, receber notificações e importar vendas dependem dessa conexão.

Fontes oficiais, consultadas em 16/10/2026:
- [Autenticação e Autorização](https://developers.mercadolivre.com.br/pt_br/autenticacao-e-autorizacao) (página atualizada em 29/12/2025);
- [Crie uma aplicação](https://developers.mercadolivre.com.br/pt_br/crie-uma-aplicacao-no-mercado-livre);
- [Gerencie seu aplicativo](https://developers.mercadolivre.com.br/pt_br/gerencie-seu-aplicativo).

O que elas definem:
- **Autorização:** fluxo *authorization code* em `https://auth.mercadolivre.com.br/authorization`, com `state` (recomendado) e PKCE (`code_challenge`, S256). O PKCE é opcional, mas passa a ser obrigatório se for ligado no app.
- **Endereço de retorno:** precisa ser HTTPS e exatamente igual ao cadastrado no app, sem partes variáveis. Se precisar levar informação, ela vai no `state`.
- **Troca e renovação:** `POST https://api.mercadolibre.com/oauth/token`, com o corpo em `application/x-www-form-urlencoded`. A resposta traz `access_token`, `expires_in` (21600 s, ou seja, 6 h), `user_id` e `refresh_token`.
- **Refresh token:** uso único, válido por 6 meses e só o último emitido vale. Cada renovação devolve um novo.
- **Erro `invalid_grant`:** o código ou o refresh é inválido, venceu ou foi revogado (troca de senha, revogação pelo vendedor, fluxos internos do ML). O acesso também é invalidado depois de 4 meses sem nenhuma chamada à API.
- **Conta de colaborador:** não autoriza o app; o ML responde `invalid_operator_user_id`.
- **Revogação:** `DELETE /users/{user_id}/applications/{app_id}`.
- **Apps por conta:** no Brasil, cada conta só pode criar **1 aplicação**, depois de validar os dados do titular.

## Decisões

### 1. Um app da plataforma; o retorno é uma tela do painel

`MercadoLivre:ClientId`, `MercadoLivre:ClientSecret` e `MercadoLivre:RedirectUri` são configuração da plataforma. O segredo fica só em variável de ambiente (`MercadoLivre__ClientSecret`), conforme a regra 3.

O endereço de retorno é a tela `/canais/mercado-livre/retorno` do painel, que é fixa e igual para todas as lojas. A tela lê `code` e `state` e repassa à API, autenticada com a sessão do Dono (`POST /api/painel/canais/mercado-livre/retorno`).

Por isso o callback não precisa de uma rota pública sem autenticação, e a loja vem da sessão, não da URL. Em produção, o painel precisa estar num endereço HTTPS fixo (pendente: VPS).

Sem as três configurações, a tela diz que o canal "ainda não está disponível nesta instalação".

### 2. `state` de uso único e PKCE sempre

A API gera:
- um `state` aleatório de 32 bytes;
- um `code_verifier` de 64 bytes (86 caracteres);
- o `code_challenge` S256.

O pedido pendente vai para `channel_authorizations`, no banco das lojas, com RLS. Lá ficam só o hash SHA-256 do `state`, o verificador, quem pediu e o vencimento de 10 minutos.

Na volta, a pendência é procurada pelo hash dentro da loja da sessão: um `state` começado em outra loja não é encontrado. A pendência é apagada antes da troca, então o mesmo `state` não serve duas vezes.

Mandamos PKCE sempre. **Recomendação:** ligar o PKCE no app quando ele for criado.

### 3. Tokens no cofre da loja; a conexão guarda só o vencimento

O par access/refresh e o vencimento ficam num segredo do cofre (RF06), do tipo `OAuthToken`, com o nome `mercado-livre:{user_id}`. Esse tipo de segredo não tem dica no painel.

`channel_connections` passa a ter:
- `account_name`, o apelido do vendedor, lido em `/users/me`;
- `token_expires_at`;
- `status_reason`;
- `updated_at`.

A conta (`user_id`) entra no mapa da plataforma (`channel_accounts`), que já roteia os avisos do inbox (RF26) e responde ao CA5. Ela pertence a uma loja só.

Decisão conservadora (**HIPÓTESE**): **uma conta do ML por loja**. Para trocar de conta, a loja desconecta antes.

### 4. Renovação com trava, numa transação própria

O refresh é de uso único. Se a API e o Worker renovassem ao mesmo tempo, o segundo gastaria um refresh já invalidado e derrubaria a conexão. Por isso, `MercadoLivreCredentials` faz assim:
1. abre um escopo e uma transação próprios, independentes da transação de quem chamou;
2. trava a linha da conexão (`SELECT … FOR UPDATE`);
3. relê o cofre;
4. só então, se ainda for preciso, chama o ML;
5. grava o novo par antes de qualquer outra coisa.

Quem esperava na trava encontra o acesso novo e não chama o ML. O teste de integração confere: 6 chamadas simultâneas resultam em 1 renovação só.

**Quando renova:**
- **Na hora de usar:** quando falta menos de 5 minutos para vencer (`AccessTokenAsync`).
- **De hora em hora, no Worker:** o `ChannelReconciliationScheduler` publica `RenewChannelCredentials` por loja, antes da reconciliação. O handler renova as conexões ativas que vencem dentro de `MercadoLivre:RenewBefore` (90 minutos). Isso dá umas 4 renovações por dia, o que mantém o refresh de 6 meses vivo, evita a regra dos 4 meses sem uso e descobre uma revogação cedo (CA2).

**Quando o ML recusa a renovação** (`invalid_grant` ou `unauthorized_client`):
- a conexão fica `ReauthRequired`, com o motivo;
- a sincronização dos anúncios para, porque o `ChannelSyncHandler` só age em conexões ativas;
- o painel mostra uma faixa para todos os perfis, usando `channelsNeedingReauth` no resumo de integrações;
- a tela Canais de venda oferece "Autorizar de novo", que reativa a mesma conexão.

**Quando o ML está fora do ar** (5xx, 429, rede) ou as credenciais do app estão inválidas: o erro é `ChannelConnectorException`, a fila tenta de novo (RF28) e a conexão continua ativa.

**Log:** toda chamada vai para o log de integração (RF29): autorizar conta, renovar acesso e revogar acesso. As mensagens nunca levam tokens nem a `error_description` do ML.

### 5. Desconectar

A desconexão segue esta ordem:
1. revoga o app na conta, com melhor esforço (a falha fica no log);
2. apaga o segredo;
3. marca a conexão como `Disabled`;
4. tira a conta do mapa da plataforma, para que outra loja possa ligá-la.

A conexão não é apagada, porque os anúncios apontam para ela. Conectar a mesma conta de novo reativa a mesma conexão.

### 6. ML simulado em dev

`MercadoLivre:UseFake` liga `FakeMercadoLivreAuth`, e a API recusa subir em produção com essa opção. A "página de autorização" fica em `/api/dev/mercado-livre/autorizar`: autorizar volta ao painel com `code=SIM-{vendedor}`, e negar volta com `error=access_denied`.

O simulado reproduz o uso único do refresh dentro de cada processo, e o teste aceita um refresh emitido por outro processo (no compose, API e Worker não compartilham memória). O contrato do cliente real (`MercadoLivreAuthClient`) é testado contra respostas no formato da documentação.

### 7. Notificações (parte 2 — RF21 CA4/CA5)

Fonte: [Notificações](https://developers.mercadolivre.com.br/pt_br/produto-receba-notificacoes) (página atualizada em 14/09/2026, consultada em 17/10/2026).

O que a documentação define:
- **Formato:** o ML faz um POST JSON na URL de notificações do app, com `resource`, `user_id` (número ou texto), `topic`, `application_id`, `attempts`, `sent` e `received`. Alguns tópicos trazem também `_id`/`id` e `actions`.
- **Resposta:** é preciso responder **200 em até 500 ms**, senão os tópicos são desativados. O ML recomenda responder logo e consultar o recurso por uma fila.
- **Novas tentativas:** duram 1 hora. Os avisos perdidos ficam 2 dias em `GET /missed_feeds?app_id=`.
- **Origem:** não há assinatura. O ML publica a lista de IPs de origem.

Decisões:
- **Endpoint:** `POST /api/webhooks/canais/mercado-livre`, cadastrado no app como URL de notificações (`https://<api da plataforma>/api/webhooks/canais/mercado-livre`). Tópicos para assinar: `orders_v2`, `items`, `questions`, `messages`, `payments`.
- **O que o endpoint faz:**
  - confere o `application_id`, que tem de ser o app da plataforma;
  - confere o IP de origem, se `MercadoLivre:NotificationIps` estiver configurado. A lista muda, por isso fica na configuração e não no código;
  - grava no inbox do RF26 e responde 200; repetido também recebe 200.
- **Chave de repetição:** o `_id`/`id` quando vem; sem ele, `tópico|recurso|received`.
- **Roteamento:** o RF26 já existente acha a loja pelo `user_id` no mapa da plataforma (CA5). Conta sem loja fica "sem dono" no inbox, com log crítico.
- **Processamento, uma vez por loja** (`MercadoLivreChannelProcessor`):
  - `items` de um anúncio da loja pede a conferência dele (`ReconcileListing`, RF27). Ela consulta o canal e corrige pelo estoque local, então o conteúdo do aviso nunca é confiado.
  - Os demais tópicos ficam em `external_events` como ignorados até as próximas etapas: pedidos (`orders_v2`, `payments`), perguntas e mensagens (RF31, E5). Não vão para o log de integração, que encheria com milhares de avisos.
  - **Pendente:** recuperar o `missed_feeds` (por exemplo, depois de a API ficar fora do ar) quando os pedidos entrarem.
- **Limite próprio:** `webhook-canal`, com 600 por minuto por IP (**HIPÓTESE**). O ML manda de dezenas de IPs e não pode receber 429.

## Consequências

- **Teste real:** exige criar o app no DevCenter com uma conta do Mercado Livre (uma aplicação por conta no Brasil) e um endereço HTTPS público para o retorno (VPS ou túnel). Usuários de teste do ML servem para homologar.
- **Rotação do segredo:** se o *Client Secret* do app for renovado no DevCenter, os acessos das lojas podem ser invalidados. A troca precisa ser coordenada com a atualização da variável de ambiente.
- **Próximos PRs:** notificações (`/api/webhooks/canais/mercado-livre`, roteadas pelo `user_id` — CA4/CA5), publicação com categoria, atributos e compatibilidades (CA3) e pedidos. Todos usam `MercadoLivreCredentials.AccessTokenAsync`.
