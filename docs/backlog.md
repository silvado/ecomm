# Backlog

> Gerado por `node tools/backlog/generate.mjs` a partir de `tools/backlog/backlog-data.mjs`. Não editar à mão.

Hierarquia Scrum do Azure Boards: **Epic → Feature → Product Backlog Item → Task**. Estimativas em horas de desenvolvimento (uma pessoa), sem buffer.

| Etapa | Horas |
|---|---:|
| E1 — Núcleo e loja (meses 1–3) | 470 |
| E2 — Canais (mês 4) | 142 |
| E3 — IA e OLX (mês 5) | 90 |
| E4 — Produto vendável (mês 6) | 156 |
| E5 — Primeiros clientes (meses 7–8) | 116 |
| **Total** | **974** |

Progresso: 226 h de 974 h em tarefas concluídas (23%).

Referência: 6 h produtivas/dia ≈ 162 dias úteis ≈ 7,7 meses de uma pessoa.

## E1 — Núcleo e loja (meses 1–3) — 470 h

Multi-tenant, catálogo, estoque, loja, checkout, frete, NF-e. Marco: piloto vendendo pelo site.

### Fundação da plataforma — 132 h

#### Estrutura da solução, ambiente local e CI — 18 h  `RNF09` · **concluído**

**Critérios de aceite:** Solução .NET (Domain, Application, Infrastructure, Api, Worker) e apps Angular compilam; docker compose sobe API, PostgreSQL, storage S3 (SeaweedFS) e Caddy; CI no GitHub Actions roda build, testes e gitleaks a cada PR.

- [x] Criar projetos .NET e referências entre camadas — 4 h
- [x] Criar apps Angular storefront (SSR) e admin — 6 h
- [x] docker-compose de desenvolvimento — 4 h
- [x] Pipeline GitHub Actions (build, testes, gitleaks) — 4 h

#### Spike: Wolverine + EF Core + RLS — 8 h  `ADR-0002` · **concluído**

**Critérios de aceite:** Protótipo publica evento pelo outbox na mesma transação do EF com RLS ativo; latência medida e registrada no ADR-0002.

- [x] Configurar Wolverine com persistência PostgreSQL — 4 h
- [x] Medir latência do outbox e documentar no ADR — 4 h

#### Isolamento multi-tenant (filtro EF + RLS) — 29 h  `RNF01` · **concluído**

**Critérios de aceite:** Teste de integração prova que nenhuma linha de outro tenant é lida ou alterada com filtro EF, sem filtro EF (só RLS) e sem tenant definido; conexão reaproveitada do pool não carrega tenant anterior; build falha se tabela de negócio estiver sem RLS.

- [x] Catálogo de tenants com database_key — 6 h
- [x] Interceptor de conexão (app.tenant_id) — 4 h
- [x] Migrações: papéis app_user/app_migrator e policies RLS — 6 h
- [x] Filtro global EF e guarda de TenantId no SaveChanges — 4 h
- [x] Testes de isolamento (3 cenários + pool) — 6 h
- [x] Verificação automática de tabelas sem RLS — 3 h

#### Resolução de tenant pelo Host — 10 h  `RF04` · **concluído**

**Critérios de aceite:** Host de subdomínio ou domínio verificado resolve o tenant com cache ≤ 60 s; Host desconhecido → 404; tenant suspenso → página de loja indisponível.

- [x] Middleware de resolução com cache — 5 h
- [x] Página de loja indisponível — 2 h
- [x] Testes de resolução — 3 h

#### Observabilidade básica — 10 h  `RNF06` · **em andamento**

**Critérios de aceite:** Logs JSON com tenant_id e correlation_id e redação de campos sensíveis; /health/live e /health/ready; métricas de fila expostas.

- [ ] Serilog estruturado com redação — 4 h
- [x] Health checks — 2 h
- [ ] Métricas OpenTelemetry da fila — 4 h

#### Cofre de segredos por tenant — 17 h  `RF06` · **em andamento**

**Critérios de aceite:** Segredos com criptografia envelope; painel mostra só metadados; teste automatizado não encontra segredos em logs; alerta de vencimento do certificado A1.

- [x] Serviço de criptografia envelope e rotação de chave mestra — 8 h
- [ ] API de cadastro e metadados — 3 h
- [x] Teste de vazamento em logs — 3 h
- [ ] Alerta de vencimento do A1 — 3 h

#### Autenticação, usuários e perfis do tenant — 24 h  `RF07` · **concluído**

**Critérios de aceite:** Login com e-mail e senha (hash forte), bloqueio após 5 tentativas, perfis Dono e Operador com permissões distintas, limite de usuários do plano respeitado.

- [x] Identidade e login no catálogo — 8 h
- [x] JWT com tenant e papel validados por vínculo — 4 h
- [x] Políticas de autorização Dono/Operador — 4 h
- [x] Telas de login e gestão de usuários — 8 h

#### Cadastro de tenant e subdomínio automático — 16 h  `RF01, RF02` · **em andamento**

**Critérios de aceite:** Superadmin cria tenant com CNPJ validado e slug único; loja acessível em slug.plataforma.com.br com HTTPS em até 1 min; tema e textos editáveis refletem em até 1 min.

- [x] API de tenant e configurações da loja — 4 h
- [x] Tela de dados da empresa e identidade visual — 6 h
- [ ] Caddy com certificado curinga (DNS-01) — 6 h

### Catálogo de peças — 80 h

#### Cadastro de peça com fotos — 32 h  `RF08` · **concluído**

**Critérios de aceite:** Peça com todos os campos do RF08; código interno único por tenant; fotos convertidas para WebP em 3 tamanhos no storage com prefixo do tenant.

- [x] Domínio e API de peça — 8 h
- [x] Upload, conversão e storage S3 de fotos — 8 h
- [x] Telas de listagem e formulário de peça — 16 h

#### Compatibilidade por veículo — 20 h  `RF09` · **concluído**

**Critérios de aceite:** Peça tem 0..n compatibilidades (marca, modelo, anos, motorização) apontando para a tabela global de veículos; busca por veículo retorna só peças compatíveis.

- [x] Tabela global de veículos e replicação para ref — 8 h
- [x] Seleção de compatibilidades no formulário — 8 h
- [x] Filtro de busca por veículo — 4 h

#### Rastreabilidade de peça usada — 10 h  `RF10`

**Critérios de aceite:** Campos de origem e NF de entrada; campos extras configuráveis; modo desmontagem torna-os obrigatórios para publicar; dados do doador nunca expostos na loja ou canais.

- [ ] Modelo e regras de rastreabilidade — 6 h
- [ ] Telas e configuração de campos extras — 4 h

#### Importação de peças por planilha — 18 h  `RF11`

**Critérios de aceite:** Modelo de planilha fornecido; validação linha a linha com relatório de erros; importação em background com progresso.

- [ ] Modelo, leitura e validação da planilha — 8 h
- [ ] Job de importação com progresso e relatório — 6 h
- [ ] Tela de importação — 4 h

### Estoque e sincronização interna — 48 h

#### Reserva atômica de estoque — 16 h  `RF12, RNF02` · **concluído**

**Critérios de aceite:** Reserva e baixa por UPDATE condicional; reserva expira no tempo do tenant (padrão 30 min); 50 requisições simultâneas pela última unidade resultam em exatamente 1 reserva.

- [x] Agregado de estoque e comandos SQL atômicos — 6 h
- [x] Expiração de reserva por mensagem agendada — 4 h
- [x] Testes de concorrência — 6 h

#### Outbox, idempotência e retentativas — 20 h  `RF25, RF26, RF28` · **em andamento**

**Critérios de aceite:** Toda alteração de estoque grava evento na mesma transação; webhook entregue 10 vezes altera o estoque uma vez; falhas retentadas com backoff e enviadas à fila de falhas com alerta após 5 tentativas; reprocessamento pelo painel.

- [x] Evento StockChanged pelo outbox — 4 h
- [ ] Inbox de eventos externos com chave única — 6 h
- [ ] Política de retentativa, fila de falhas e alerta — 4 h
- [ ] Tela de eventos em falha com reprocessamento — 6 h

#### Log de integração consultável — 12 h  `RF29`

**Critérios de aceite:** Toda chamada externa registrada sem segredos; consulta por peça e por pedido no painel; retenção de 90 dias.

- [ ] Registro de log de integração — 4 h
- [ ] Telas de consulta por peça e pedido — 6 h
- [ ] Job de retenção — 2 h

### Loja virtual — 120 h

#### Vitrine com SSR, tema por tenant e busca — 32 h  `RF13` · **concluído**

**Critérios de aceite:** Páginas renderizadas no servidor com tema do tenant; busca por texto, código e veículo; JSON-LD e Open Graph por peça; Lighthouse mobile ≥ 85 em performance e SEO.

- [x] SSR com resolução de tenant e tema via CSS custom properties — 12 h
- [x] Listagem e busca (texto, código, veículo) — 12 h
- [x] Página de produto com SEO — 8 h

#### Carrinho, checkout e frete — 34 h  `RF14` · **em andamento**

**Critérios de aceite:** Carrinho e checkout como convidado ou com conta; frete calculado pelo Melhor Envio com CEP de origem do tenant; retirada no balcão configurável.

- [x] Carrinho — 8 h
- [ ] Checkout — 12 h *(em andamento)*
- [ ] IShippingProvider e adapter Melhor Envio — 12 h *(em andamento)*
- [x] Opção de retirada — 2 h

#### Pagamento por gateway do tenant — 42 h  `RF15`

**Critérios de aceite:** Gateway escolhido por tenant; cartão tokenizado no gateway; pedido pago só por webhook validado; webhook repetido sem efeito duplicado; Pix quando o gateway suportar.

- [ ] IPaymentGateway e fake — 4 h
- [ ] Adapter Mercado Pago (cartão e Pix) — 16 h
- [ ] Adapter Cielo E-commerce — 16 h
- [ ] Webhooks de pagamento com validação e idempotência — 6 h

#### Área do cliente final — 12 h  `RF16`

**Critérios de aceite:** Acesso por login ou link mágico; cliente vê só os próprios pedidos daquele tenant, com status e rastreio.

- [ ] Link mágico por e-mail — 6 h
- [ ] Lista e detalhe de pedidos — 6 h

### Pedidos e fiscal — 70 h

#### Painel de pedidos unificado — 16 h  `RF17`

**Critérios de aceite:** Pedidos de todos os canais num painel com origem e filtros; pedido externo único por tenant, canal e id externo.

- [ ] API de pedidos com filtros — 6 h
- [ ] Telas de lista e detalhe de pedido — 10 h

#### Venda de balcão — 8 h  `RF18`

**Critérios de aceite:** Lançamento de venda com baixa atômica de estoque e evento de sincronização.

- [ ] Caso de uso e tela de venda de balcão — 8 h

#### Emissão de NF-e — 36 h  `RF19`

**Critérios de aceite:** Emissão via IFiscalProvider com certificado do cofre; status, DANFE e XML no pedido; rejeição mostra motivo e permite reenviar; dados fiscais configuráveis por peça com padrão por tenant.

- [ ] IFiscalProvider e fake — 4 h
- [ ] Adapter do provedor fiscal escolhido — 20 h
- [ ] Configuração fiscal de tenant e peça — 6 h
- [ ] Tela de emissão e acompanhamento — 6 h

#### Cancelamento e devolução — 10 h  `RF20`

**Critérios de aceite:** Cancelamento ou devolução retorna quantidades ao estoque e gera evento; opção de não retornar peça avariada; cancelamento de NF-e respeita prazo legal.

- [ ] Casos de uso de cancelamento e devolução — 6 h
- [ ] Telas e integração com NF-e de devolução — 4 h

### Operação em produção — 20 h

#### Implantação na VPS e backup — 20 h  `RNF05, RNF07, RNF08`

**Critérios de aceite:** Compose de produção com segredos por variável de ambiente; backup diário do banco fora do servidor; restauração testada e documentada.

- [ ] Compose de produção e Caddyfile — 8 h
- [ ] Backup diário do PostgreSQL e do storage — 8 h
- [ ] Runbook de implantação e restauração — 4 h

## E2 — Canais (mês 4) — 142 h

Mercado Livre e Instagram (catálogo Meta) sincronizados com o estoque único.

### Conector de canais comum — 34 h

#### IChannelConnector e propagação de estoque — 22 h  `RF25, RF24`

**Critérios de aceite:** Interface comum (publicar, atualizar, pausar, retomar, eventos, estado remoto); handler de StockChanged atualiza todos os canais ativos; peça zerada pausa anúncios em ≤ 60 s; reativa só o que o sistema pausou.

- [ ] Interface, fake e testes de contrato genéricos — 8 h
- [ ] Handler de propagação por canal — 8 h
- [ ] Regras de pausa/reativação e métrica de latência — 6 h

#### Reconciliação horária — 12 h  `RF27`

**Critérios de aceite:** Job horário compara quantidade, preço e status dos anúncios com o estoque local, corrige divergências e registra no log, respeitando limites de taxa.

- [ ] Job de reconciliação por tenant e canal — 12 h

### Mercado Livre — 84 h

#### Conexão OAuth do vendedor — 12 h  `RF21`

**Critérios de aceite:** Um app para todos os tenants; autorização OAuth por tenant; tokens no cofre; renovação automática; falha marca conexão para reautorizar e alerta.

- [ ] Fluxo OAuth e armazenamento de tokens — 8 h
- [ ] Renovação automática e alertas — 4 h

#### Webhooks do Mercado Livre — 8 h  `RF21, RF26`

**Critérios de aceite:** Endpoint responde 2xx imediatamente; evento gravado com chave única; tenant resolvido pelo user_id; processamento busca o recurso na API.

- [ ] Endpoint, inbox e roteamento por vendedor — 8 h

#### Publicação de anúncios com atributos e compatibilidades — 34 h  `RF21, RF09`

**Critérios de aceite:** Publica peça com categoria, atributos obrigatórios e compatibilidades criadas pelo vendedor; atualiza preço/quantidade; pausa e reativa.

- [ ] Escolha de categoria e atributos obrigatórios — 16 h
- [ ] Envio de compatibilidades — 12 h
- [ ] Atualização, pausa e reativação — 6 h

#### Pedidos do Mercado Livre no painel — 12 h  `RF17, RF21`

**Critérios de aceite:** Venda no ML cria pedido unificado, baixa o estoque atomicamente e propaga; venda sem saldo vira conflito de estoque com alerta.

- [ ] Processamento de orders_v2 e pagamentos — 12 h

#### Importação dos anúncios existentes — 12 h  `RF11`

**Critérios de aceite:** Lista anúncios do vendedor e cria peças vinculadas sem duplicar em reimportação.

- [ ] Importação de anúncios com vínculo — 12 h

#### Recebimento de perguntas — 6 h  `RF31`

**Critérios de aceite:** Perguntas do ML aparecem no painel vinculadas à peça e podem ser respondidas manualmente.

- [ ] Ingestão e resposta manual de perguntas — 6 h

### Instagram / catálogo Meta — 24 h

#### Catálogo Meta por tenant — 24 h  `RF22, RF24`

**Critérios de aceite:** Feed por tenant em URL com token; disponibilidade e preço atualizados em segundos pela Catalog Batch API; produto leva à página da peça na loja.

- [ ] Feed de catálogo — 8 h
- [ ] Atualizações pela Catalog Batch API — 10 h
- [ ] Conexão e configuração do catálogo — 6 h

## E3 — IA e OLX (mês 5) — 90 h

Cadastro assistido por IA, preço sugerido e integração OLX via hub.

### Fundação de IA — 16 h

#### IAiProvider com auditoria — 16 h  `RF35`

**Critérios de aceite:** Implementação Anthropic e fake; toda chamada registrada com modelo, tokens, custo, entrada resumida e saída; a camada de IA não tem acesso a comandos de escrita.

- [ ] IAiProvider, adapter Anthropic e fake — 8 h
- [ ] Decorador de auditoria — 4 h
- [ ] Contador de uso por funcionalidade — 4 h

### Cadastro assistido e preço — 46 h

#### Cadastro de peça a partir de fotos — 30 h  `RF30`

**Critérios de aceite:** Fotos + código geram rascunho com título, descrição, categoria, atributos e compatibilidades sugeridas; campos da IA destacados até revisão; publicação só por humano.

- [ ] Prompt e saída estruturada validada — 12 h
- [ ] Tela de revisão do rascunho — 12 h
- [ ] Conjunto de avaliação de qualidade — 6 h

#### Preço sugerido — 16 h  `RF32`

**Critérios de aceite:** Sugere faixa de preço mostrando a amostra usada; nunca altera o preço sozinho.

- [ ] Coleta de anúncios semelhantes (fonte a validar) — 10 h
- [ ] Cálculo e tela de sugestão — 6 h

### OLX — 28 h

#### Integração OLX via hub — 28 h  `RF23`

**Critérios de aceite:** Hub escolhido e documentado em ADR; adapter implementa IChannelConnector e passa nos testes de contrato.

- [ ] Spike e ADR de escolha do hub — 4 h
- [ ] Adapter do hub OLX — 24 h

## E4 — Produto vendável (mês 6) — 156 h

Onboarding, domínio próprio, assinatura, superadmin e LGPD.

### Onboarding e domínio próprio — 46 h

#### Assistente de onboarding — 24 h  `RF05`

**Critérios de aceite:** Passos salvos parcialmente e retomáveis; canais aparecem conforme o plano; loja só publica com empresa, gateway e frete concluídos.

- [ ] Fluxo e estado do onboarding — 8 h
- [ ] Telas dos passos — 16 h

#### Domínio próprio com SSL automático — 22 h  `RF03`

**Critérios de aceite:** Painel mostra registros DNS exatos; verificação a cada 5 min por até 72 h; certificado emitido só para domínio verificado via endpoint ask; MX nunca tocado.

- [ ] Job de verificação DNS — 10 h
- [ ] Endpoint ask interno — 4 h
- [ ] Tela de domínio com instruções — 8 h

### Assinatura e planos — 50 h

#### Planos, limites e feature flags — 20 h  `RF37, RF36`

**Critérios de aceite:** Planos, limites e recursos são dados; mudança de plano tem efeito imediato; limite de IA bloqueia ou consome pacote excedente.

- [ ] Modelo de planos e verificação de limites — 12 h
- [ ] Bloqueio e pacote excedente de IA — 8 h

#### Cobrança recorrente e inadimplência — 30 h  `RF38`

**Critérios de aceite:** Cobra mensalidade e implantação pelo gateway da plataforma; bloqueio gradual por inadimplência; pagamento reativa automaticamente.

- [ ] ISubscriptionBilling e adapter — 20 h
- [ ] Régua de inadimplência — 10 h

### Superadmin e LGPD — 60 h

#### Painel de superadmin — 26 h  `RF07`

**Critérios de aceite:** Gestão de tenants e planos; MFA obrigatório; todo acesso a dados de tenant auditado.

- [ ] Telas de superadmin — 16 h
- [ ] MFA — 6 h
- [ ] Auditoria de acesso — 4 h

#### LGPD e exportação de dados — 34 h  `RNF04, RF39`

**Critérios de aceite:** Termos e política por tenant; anonimização de cliente sob pedido; log de acesso a dados pessoais; exportação completa em ZIP com link que expira em 7 dias.

- [ ] Termos e política de privacidade — 6 h
- [ ] Anonimização de cliente final — 10 h
- [ ] Log de acesso a dados pessoais — 6 h
- [ ] Exportação completa do tenant — 12 h

## E5 — Primeiros clientes (meses 7–8) — 116 h

IA de atendimento, resumo diário, guardião da sincronização e 3–5 clientes beta.

### IA de atendimento e alertas — 76 h

#### Respostas a compradores — 52 h  `RF31`

**Critérios de aceite:** Usa só dados do cadastro; classifica em simples (automática se habilitada) ou complexa (rascunho); nunca afirma compatibilidade ausente, verificado por conjunto de avaliação.

- [ ] Respostas a perguntas do ML — 16 h
- [ ] Classificação e modo automático — 10 h
- [ ] Conjunto de avaliação — 10 h
- [ ] Mensagens do Instagram (após App Review) — 16 h

#### Resumo diário e guardião da sincronização — 24 h  `RF33, RF34`

**Critérios de aceite:** Resumo diário no horário do tenant com números de consultas determinísticas; diagnóstico de falhas com ação sugerida enviado por e-mail.

- [ ] Resumo diário — 12 h
- [ ] Guardião da sincronização — 12 h

### Programa beta — 40 h

#### Implantação de 3 a 5 clientes beta — 40 h  `—`

**Critérios de aceite:** Clientes beta configurados pelo onboarding sem código específico; feedback registrado no backlog.

- [ ] Acompanhamento de implantação — 24 h
- [ ] Correções e ajustes do beta — 16 h
