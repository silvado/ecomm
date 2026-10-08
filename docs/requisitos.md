# Requisitos

Origem: [BRIEF.md](BRIEF.md) §4 e §5. Cada RF tem critérios de aceite (CA) verificáveis. Itens marcados **HIPÓTESE** seguem a opção mais conservadora até resposta do dono do produto — ver [Em aberto](#em-aberto).

Legenda de etapa (roadmap §6): **E1** Núcleo e loja · **E2** Canais · **E3** IA e OLX · **E4** Produto vendável · **E5** Primeiros clientes.

---

## 1. Multi-tenant e onboarding

### RF01 — Cadastro de tenant · E1 (mínimo) / E4 (completo)
CNPJ, razão social, nome fantasia, logo, cores, textos institucionais.
- CA1: CNPJ é validado (dígitos verificadores) e único na plataforma.
- CA2: logo aceita PNG/JPG/SVG/WebP até 2 MB; SVG é sanitizado antes de servir.
- CA3: alterar cores/logo/textos reflete na loja em até 1 min (invalidação de cache), sem novo deploy.
- CA4: as cores são validadas quanto a contraste mínimo WCAG AA para texto sobre fundo; o painel avisa (não bloqueia).

### RF02 — Subdomínio automático · E1
- CA1: ao criar o tenant, o slug gera `slug.plataforma.com.br`, acessível com HTTPS em até 1 min.
- CA2: slug é único, minúsculo, `[a-z0-9-]`, 3–40 caracteres, e não pode usar palavras reservadas (`www`, `api`, `admin`, `app`, `mail`, …).
- CA3: o subdomínio continua funcionando mesmo após ativar domínio próprio (redireciona 301 para o domínio principal).

### RF03 — Domínio próprio · E4
- CA1: o painel exibe os registros exatos a criar: `CNAME www → slug.plataforma.com.br` e, para o domínio raiz, o registro indicado no [ADR-0004](adr/0004-proxy-ssl-dominios.md).
- CA2: o sistema verifica a propagação (consulta DNS) a cada 5 min por até 72 h e mostra o status (pendente / verificado / falhou).
- CA3: só após verificado o domínio entra na lista consultada pelo endpoint `ask` do Caddy; antes disso nenhum certificado é emitido.
- CA4: o sistema **nunca** solicita, altera ou exibe instruções sobre registros MX/TXT de e-mail do cliente.
- CA5: remover o domínio tira-o da lista `ask` imediatamente; o subdomínio volta a ser o principal.

### RF04 — Resolução de tenant por Host · E1
- CA1: toda requisição da loja/API pública resolve o tenant pelo cabeçalho `Host` (subdomínio ou domínio próprio verificado), com cache em memória (TTL ≤ 60 s).
- CA2: Host desconhecido retorna 404 sem revelar existência de outros tenants.
- CA3: tenant suspenso (RF38) exibe página de "loja indisponível", sem checkout.
- CA4: o painel admin resolve o tenant pelo usuário autenticado, **não** pelo Host.

### RF05 — Assistente de onboarding · E4
Passos: empresa → domínio → gateway → fiscal → frete → Mercado Livre → Instagram → OLX.
- CA1: cada passo pode ser salvo parcialmente e retomado; o progresso aparece no painel.
- CA2: passos de canais são opcionais e só aparecem se o plano os habilitar (RF37).
- CA3: a loja só pode ser publicada com empresa, gateway e frete concluídos.

### RF06 — Cofre de segredos por tenant · E1
- CA1: segredos (chaves de gateway, certificado A1 + senha, tokens OAuth) são gravados com criptografia envelope (chave de dados por tenant, protegida por chave mestra fora do banco).
- CA2: nenhum segredo aparece em logs, respostas de API, exportações (RF39) ou mensagens de erro — verificado por teste automatizado que procura valores conhecidos nos logs.
- CA3: o painel mostra apenas metadados (tipo, últimos 4 caracteres, validade); valores nunca são devolvidos ao front.
- CA4: rotação da chave mestra re-encripta as chaves de dados sem downtime.
- CA5: o certificado A1 exibe alerta 30, 15 e 5 dias antes de vencer.

### RF07 — Usuários e perfis · E1 (dono/operador) / E4 (superadmin)
- CA1: perfis por tenant: **Dono** (tudo, inclusive cofre, plano e usuários) e **Operador** (catálogo, pedidos, atendimento; sem cofre, plano, usuários, exportação).
- CA2: número de usuários respeita o limite do plano. **HIPÓTESE:** 2 / 5 / 10 (Q17).
- CA3: superadmin da plataforma é um papel separado, com MFA obrigatório; todo acesso de superadmin a dados de um tenant é registrado em log de auditoria.
- CA4: login com e-mail + senha (hash Argon2id/PBKDF2), MFA opcional para Dono; bloqueio após 5 tentativas. **HIPÓTESE:** bloqueio de 15 min, senha de 10+ caracteres, sem recuperação por e-mail por enquanto (Q18, Q19).

## 2. Catálogo e estoque

### RF08 — Cadastro de peça · E1
- CA1: campos: título, descrição, fotos (1–20), estado (nova/usada/recondicionada), preço, quantidade, dimensões (cm) e peso (kg) da embalagem, código interno, código OEM (0..n).
- CA2: código interno é único por tenant.
- CA3: fotos são convertidas para WebP em 3 tamanhos e guardadas no storage com caminho prefixado pelo tenant.
- CA4: peça sem dimensões/peso não pode ser publicada em canal que exija frete calculado.

### RF09 — Compatibilidade por veículo · E1 (loja) / E2 (ML)
- CA1: uma peça tem 0..n compatibilidades (marca, modelo, ano inicial–final, motorização).
- CA2: a tabela de veículos é **da plataforma** (compartilhada, sem `tenant_id`), mantida pelo superadmin e/ou importada da árvore do ML.
- CA3: busca da loja por veículo retorna só peças compatíveis com o veículo escolhido.
- CA4: na publicação no ML, as compatibilidades criadas pelo vendedor são enviadas pela API de compatibilidades (ver [Fontes](#fontes)).

### RF10 — Rastreabilidade de peça usada · E1
- CA1: peça usada tem: origem (veículo doador: placa/chassi parcial, Renavam, certidão de baixa), NF de entrada, e campos extras configuráveis por tenant.
- CA2: se o tenant marcar "empresa de desmontagem (Lei 12.977)", esses campos tornam-se obrigatórios para publicar. **HIPÓTESE:** opcionais por padrão.
- CA3: dados do veículo doador nunca aparecem na loja nem nos canais.

### RF11 — Importação inicial · E1 (planilha) / E2 (ML)
- CA1: importação por planilha (modelo XLSX/CSV fornecido) valida linha a linha e gera relatório de erros sem importar parcialmente sem aviso.
- CA2: importação do ML lista os anúncios ativos/pausados do vendedor, cria peças vinculadas (`ChannelListing`) e não duplica em reimportação.
- CA3: importação roda em background com progresso visível.

### RF12 — Reserva atômica de estoque · E1
- CA1: ao iniciar pagamento, a quantidade é reservada atomicamente; se não houver saldo, o checkout informa indisponibilidade.
- CA2: reserva expira em tempo configurável por tenant (padrão 30 min); a expiração devolve o saldo e gera evento de estoque.
- CA3: pagamento confirmado converte a reserva em baixa definitiva; confirmação após expiração tenta nova reserva e, sem saldo, marca o pedido para estorno.
- CA4: teste com 50 requisições simultâneas para a última unidade resulta em exatamente 1 reserva (RNF02).

## 3. Loja virtual

### RF13 — Vitrine · E1
- CA1: responsiva (360 px a 1920 px), Lighthouse ≥ 85 em performance e SEO no mobile.
- CA2: renderizada no servidor (SSR) com título, descrição, Open Graph e JSON-LD `Product` por peça.
- CA3: busca por texto (com tolerância a acentos), por código interno/OEM e por veículo (marca → modelo → ano).
- CA4: peças sem estoque aparecem como indisponíveis ou ocultas, conforme configuração.

### RF14 — Carrinho, checkout e frete · E1
- CA1: frete calculado por `IShippingProvider` (implementação inicial Melhor Envio) com CEP de origem do tenant e dimensões/peso das peças.
- CA2: opção de retirada no balcão configurável.
- CA3: checkout como convidado ou com conta; coleta só os dados necessários (RNF04).

### RF15 — Pagamento · E1
- CA1: gateway escolhido por tenant via `IPaymentGateway`; implementações iniciais Mercado Pago e Cielo E-commerce.
- CA2: dados de cartão nunca passam pelos nossos servidores em claro (tokenização/checkout do gateway).
- CA3: o pedido só vira "pago" por webhook validado (assinatura/consulta de confirmação ao gateway), nunca pelo retorno do navegador.
- CA4: webhooks repetidos não duplicam efeito (RF26).
- CA5: Pix suportado se o gateway oferecer. **HIPÓTESE:** Pix e cartão no escopo E1.

### RF16 — Área do cliente final · E1
- CA1: lista de pedidos com status e rastreio; acesso por login ou link mágico enviado por e-mail.
- CA2: cliente final só vê pedidos do próprio e-mail **e** do tenant da loja acessada.

## 4. Pedidos e fiscal

### RF17 — Pedidos unificados · E1 (site, balcão) / E2 (ML) / E3 (OLX)
- CA1: pedidos de todos os canais num único painel com coluna "origem" e filtros por origem/status/período.
- CA2: pedido externo guarda o id do canal e é único por (tenant, canal, id externo).

### RF18 — Venda de balcão · E1
- CA1: lançamento com peças, quantidade, forma de pagamento e cliente opcional.
- CA2: dá baixa atômica no estoque e gera evento de sincronização.

### RF19 — NF-e · E1
- CA1: emissão via `IFiscalProvider`, com certificado A1 do cofre; status (processando/autorizada/rejeitada) e DANFE/XML disponíveis no pedido.
- CA2: rejeição mostra o motivo da SEFAZ e permite corrigir e reenviar.
- CA3: dados fiscais por peça (NCM, CFOP, CST/CSOSN, origem) configuráveis com padrão por tenant.
- CA4: **HIPÓTESE:** emissão manual (botão) na E1; automática após pagamento como opção posterior.

### RF20 — Cancelamento e devolução · E1
- CA1: cancelar pedido pago ou registrar devolução devolve as quantidades ao estoque e gera evento (republica/despausa nos canais).
- CA2: devolução de peça usada permite marcar "não retorna ao estoque" (avaria).
- CA3: cancelamento de NF-e respeita o prazo legal; fora dele, orienta NF-e de devolução.

## 5. Integrações com canais

Todos os conectores implementam `IChannelConnector`: `Publish`, `UpdatePriceAndQuantity`, `Pause`, `Resume`, `HandleEvent`, `GetRemoteState` (para reconciliação).

### RF21 — Mercado Livre · E2
- CA1: um único app no DevCenter; cada tenant autoriza via OAuth 2.0 (authorization code); tokens no cofre.
- CA2: o refresh token é renovado antes de expirar; falha de renovação gera alerta e marca a conexão como "reautorizar".
- CA3: publicação com categoria, atributos obrigatórios da categoria e compatibilidades.
- CA4: webhooks (`orders_v2`, `items`, `questions`, `messages`, `payments`) respondem 2xx imediatamente, gravam o evento bruto e enfileiram; o processamento busca o recurso na API (`GET resource`).
- CA5: a notificação é associada ao tenant pelo `user_id` do vendedor.

### RF22 — Instagram / catálogo Meta · E2
- CA1: feed de catálogo por tenant em URL pública não adivinhável (token), no formato aceito pelo Commerce Manager.
- CA2: preço e disponibilidade atualizados no feed imediatamente; para zerar estoque em segundos (RF24) usa-se a Catalog Batch API (`items_batch`), pois o feed agendado é no máximo horário.
- CA3: o link do produto leva à página da peça na loja do tenant.

### RF23 — OLX via hub · E3
- CA1: adapter isolado `OlxHubConnector` implementando `IChannelConnector`, com contrato definido após escolha do hub.
- CA2: até a escolha, existe só o fake e os testes de contrato genéricos.

### RF24 — Pausa ao zerar estoque · E2
- CA1: quando o disponível chega a 0, todos os anúncios ativos da peça são pausados em ≤ 60 s (meta: ≤ 10 s) em operação normal.
- CA2: quando volta a ter saldo, os anúncios pausados **pelo sistema** são reativados; os pausados manualmente, não.

## 6. Sincronização

### RF25 — Eventos via outbox · E1
- CA1: toda alteração de estoque grava, na mesma transação, um registro no outbox.
- CA2: um worker publica os eventos e um handler por canal ativo atualiza o anúncio.
- CA3: falha do worker não perde eventos (são reprocessados ao reiniciar).

### RF26 — Idempotência · E1
- CA1: todo evento externo (webhook) tem chave única (ex.: `ml:{topic}:{resource}:{sent}` ou id do evento do gateway) gravada com restrição `UNIQUE`.
- CA2: o mesmo webhook entregue 10 vezes altera o estoque uma única vez (teste automatizado).
- CA3: handlers internos também são idempotentes (chave por evento do outbox).

### RF27 — Reconciliação horária · E2
- CA1: job horário compara quantidade/preço/status de cada anúncio externo com o estoque local.
- CA2: divergência é corrigida no canal (o estoque local é a verdade) e registrada no log de integração.
- CA3: o job respeita limites de taxa das APIs (lotes e espera).

### RF28 — Retentativas e alerta · E1
- CA1: falhas transitórias são retentadas com backoff exponencial com jitter (ex.: 10 s, 30 s, 2 min, 10 min, 30 min).
- CA2: após N falhas seguidas (padrão 5) o evento vai para "falhas" e gera alerta ao tenant e à plataforma.
- CA3: eventos em falha podem ser reprocessados pelo painel.

### RF29 — Log de integração · E1
- CA1: cada chamada a canal registra tenant, canal, operação, peça/pedido, status, duração, resumo de erro (sem segredos), correlação.
- CA2: consultável por peça e por pedido no painel; retenção 90 dias.

## 7. IA

### RF30 — Cadastro assistido · E3
- CA1: a partir de 1–10 fotos + código, gera título, descrição, categoria e atributos do ML e sugestão de compatibilidade.
- CA2: o resultado é sempre um **rascunho**; publicação exige ação humana.
- CA3: campos sugeridos pela IA aparecem destacados até serem revisados.
- CA4: consome 1 unidade do limite mensal do plano (RF36).

### RF31 — Respostas a compradores · E5
- CA1: responde usando apenas dados do cadastro (título, descrição, atributos, compatibilidade, estoque, políticas da loja).
- CA2: classifica a pergunta em simples (resposta automática, se o tenant habilitar) ou complexa (rascunho para revisão).
- CA3: nunca afirma compatibilidade ausente do cadastro; nesse caso, gera rascunho. Verificado por conjunto de testes de avaliação.
- CA4: **HIPÓTESE:** resposta automática desligada por padrão.

### RF32 — Preço sugerido · E3/E5
- CA1: sugere faixa de preço com base em anúncios semelhantes e mostra a amostra usada.
- CA2: nunca altera o preço; o usuário aceita ou não.
- CA3: **Em aberto:** fonte dos anúncios semelhantes (ver Q09).

### RF33 — Guardião da sincronização · E5
- CA1: analisa falhas recentes do log de integração e gera diagnóstico em linguagem simples com ação sugerida.
- CA2: envia alerta por e-mail; WhatsApp quando houver provedor definido (Q12).

### RF34 — Resumo diário · E5
- CA1: enviado em horário configurável (padrão 07:00 America/Sao_Paulo) com vendas por canal (ontem), peças sem venda há > 90 dias e perguntas pendentes.
- CA2: os números do resumo vêm de consultas determinísticas; a IA só redige o texto.

### RF35 — Auditoria e limites da IA · E3
- CA1: toda chamada registra tenant, usuário, funcionalidade, modelo, tokens, custo estimado, entrada (resumida) e saída.
- CA2: a camada de IA não tem acesso a comandos que alterem preço, estoque ou pedidos — garantido por arquitetura (a IA devolve sugestões; só casos de uso humanos aplicam).

### RF36 — Limites de uso de IA · E4
- CA1: contador mensal por tenant e por funcionalidade.
- CA2: ao atingir o limite, bloqueia novas chamadas com mensagem clara, ou consome pacote excedente se contratado.

## 8. Assinatura

### RF37 — Planos e feature flags · E4
- CA1: planos Essencial, Profissional, Completo com limites (peças ativas, usuários, cadastros com IA/mês) e recursos habilitados.
- CA2: limites e recursos são dados (tabela), não código; mudar o plano de um tenant tem efeito imediato.

### RF38 — Cobrança recorrente · E4
- CA1: cobra mensalidade e taxa de implantação via gateway **da plataforma**.
- CA2: inadimplência gradual: D+3 aviso; D+10 painel somente leitura; D+20 loja e sincronização suspensas; D+60 tenant marcado para encerramento. **HIPÓTESE** de prazos.
- CA3: pagamento regulariza o acesso automaticamente.

### RF39 — Exportação de dados · E4
- CA1: o Dono gera um pacote (ZIP) com peças, fotos, pedidos, clientes e notas em CSV/JSON + arquivos.
- CA2: o link de download expira em 7 dias e o acesso é registrado.
- CA3: segredos do cofre não são exportados.

---

## Requisitos não funcionais

| Id | Requisito | Critério de aceite |
|---|---|---|
| RNF01 | Isolamento total entre tenants | Teste de integração prova que (a) com filtro EF, (b) **sem** filtro EF (só RLS) e (c) sem `app.tenant_id` definido, nenhuma linha de outro tenant é retornada ou alterada. Teste roda no CI. |
| RNF02 | Sem venda dupla | Teste de concorrência (N threads, última unidade) → exatamente 1 sucesso, saldo nunca negativo (`CHECK (disponivel >= 0)`). |
| RNF03 | Propagação ≤ 60 s | Métrica "idade do evento no outbox ao ser concluído" p95 < 60 s; alerta se p95 > 60 s por 5 min. |
| RNF04 | LGPD | Política de privacidade e termos por tenant; coleta mínima; exclusão/anonimização de cliente final sob pedido em ≤ 15 dias; log de acesso a dados pessoais. |
| RNF05 | Segredos fora do repositório | Scanner de segredos (gitleaks) no CI bloqueia merge. |
| RNF06 | Observabilidade | Logs estruturados JSON; `/health/live` e `/health/ready`; métricas de fila (pendentes, idade, falhas); alertas. |
| RNF07 | Backup diário fora do servidor | `pg_dump` diário + sync do storage para provedor externo; teste de restauração mensal documentado. |
| RNF08 | VPS 4 vCPU / 8 GB; tenant grande em banco próprio | Catálogo de tenants guarda a conexão de cada tenant; nenhum código assume banco único. |
| RNF09 | Testes | Unitários (Domain), integração (Testcontainers/PostgreSQL), contratos dos adapters (mocks HTTP). |
| RNF10 | Idioma | Código em inglês; textos de negócio e docs em pt-BR. |

---

## Em aberto

Cada pergunta tem uma hipótese adotada até a resposta. Quem responder, atualize aqui e remova o marcador **HIPÓTESE** do RF correspondente.

| # | Pergunta | Hipótese adotada (conservadora) | Impacta |
|---|---|---|---|
| Q01 | Venda simultânea no ML e no site dentro da janela de propagação (o ML confirma vendas que não podemos bloquear). Qual canal perde? | O pedido que chegar depois é marcado "conflito de estoque" e o lojista decide; nenhum cancelamento automático. | RF12, RF24, RNF02 |
| Q02 | O piloto é empresa de desmontagem credenciada no DETRAN (Lei 12.977) ou só revende? Em qual UF? | Campos de rastreabilidade opcionais, com modo "desmontagem" que os torna obrigatórios. | RF10 |
| Q03 | Provedor fiscal (Focus NFe, PlugNotas, eNotas, NFE.io…)? | Só `IFiscalProvider` + fake até a escolha; ADR quando definido. | RF19 |
| Q04 | Regime tributário do piloto e tributação da peça usada (CSOSN/CST, CFOP) — validar com contador. | Configurável por tenant e por peça; sem padrão "inteligente". | RF19 |
| Q05 | Gateway da plataforma para cobrar a assinatura (Asaas, Iugu, Mercado Pago assinaturas…)? | Interface `ISubscriptionBilling` + fake. | RF38 |
| Q06 | Pix entra no E1? | Sim, via gateway do tenant. | RF15 |
| Q07 | Hub OLX? | Adapter fake. | RF23 |
| Q08 | Mensagens do Instagram (RF31) exigem App Review da Meta para atender vários negócios (Advanced Access). Aceitamos o prazo incerto? | RF31 inicia só com perguntas do ML; Instagram após aprovação. | RF31 |
| Q09 | Fonte de "anúncios semelhantes" para o preço sugerido — a API de busca pública do ML pode ter restrições. | Usar histórico do próprio tenant + itens retornados por API autorizada; validar na E3. | RF32 |
| Q10 | Domínio raiz: o provedor DNS do cliente suporta ALIAS/ANAME/CNAME flattening? | Recomendar redirecionar raiz → `www`; registro A só como alternativa, com IP estável (IP flutuante). | RF03 |
| Q11 | Nome/domínio da plataforma (`plataforma.com.br` é provisório). | Placeholder configurável. | RF02 |
| Q12 | Provedor de WhatsApp para alertas (API oficial Cloud API ou parceiro)? | Só e-mail na primeira versão. | RF33 |
| Q13 | Prazos de inadimplência e se a loja deve continuar vendendo durante a carência. | Ver RF38 CA2. | RF38 |
| Q14 | Uma peça pode ter mais de 1 unidade (peças novas) ou usadas são sempre quantidade 1? | Quantidade genérica (≥ 0) para todas. | RF08, RF12 |
| Q15 | Variações (lado esquerdo/direito, cor) como produtos distintos? | Sem variações: cada variação é uma peça. | RF08 |
| Q16 | Transportadoras além do Melhor Envio; Mercado Envios para vendas do ML é gerido pelo ML? | Melhor Envio no site; envio das vendas do ML fica com o ML. | RF14 |
| Q17 | Quantos usuários cada plano permite? | Essencial 2, Profissional 5, Completo 10 (tabela `plan_limits`, muda sem deploy). Loja nova começa no Essencial até existir assinatura (E4). | RF07, RF37 |
| Q18 | Regras de bloqueio e senha do painel. | Bloqueio de 15 min após 5 senhas erradas seguidas (o Dono pode desbloquear); 10 tentativas de login por minuto por IP; senha de 10 a 128 caracteres, sem regras de composição; sessão expira após 7 dias sem uso. | RF07 |
| Q19 | Como o lojista recupera a senha esquecida? Não há provedor de e-mail ainda. | O Dono cadastra usuários com senha provisória (troca obrigatória no primeiro acesso) e pode removê-los e cadastrá-los de novo. Recuperação por e-mail entra quando houver provedor (RF33/E4). | RF07 |

## Fontes

- Mercado Livre — Compatibilidades de autopeças: https://developers.mercadolivre.com.br/pt-br/compatibilidades-itens-e-produtos-de-autopecas (mudanças em 15/07/2026: compatibilidades vindas do catálogo ficam só para leitura resumida via API; as criadas pelo vendedor seguem gerenciáveis).
- Mercado Livre — Notificações: https://developers.mercadolibre.com.ar/en_us/products-receive-notifications (`orders_v2` recomendado; notificação traz `resource` a ser consultado).
- Meta — Catalog Batch API: https://developers.facebook.com/docs/marketing-api/catalog-batch (recomendada para atualizações mais frequentes que 1 h).
- Meta — App Review Instagram: https://developers.facebook.com/docs/instagram-platform/app-review
- Lei 12.977/2014: https://legis.senado.leg.br/norma/584788/publicacao/15612419 ; Resolução CONTRAN 611/2016.
