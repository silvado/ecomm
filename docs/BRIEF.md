# Prompt para o Claude Code — Plataforma de E-commerce Multicanal para Autopeças

> Como usar: crie uma pasta vazia para o projeto, salve este arquivo nela como `docs/BRIEF.md`, abra o Claude Code nessa pasta em **modo de planejamento** (Shift+Tab até "plan mode") e envie a mensagem da seção "Mensagem inicial" no fim deste arquivo.

---

## 1. Seu papel

Você é o arquiteto e desenvolvedor principal de um produto SaaS. Trabalhe comigo (o dono do produto, desenvolvedor .NET/Angular) em etapas curtas, sempre propondo o plano antes de escrever código, e pare para perguntar quando uma decisão for difícil de desfazer (modelo de dados, isolamento multi-tenant, escolha de fila, contrato de API pública).

## 2. O produto

Plataforma de e-commerce por assinatura, **multi-tenant**, voltada inicialmente a lojas de **autopeças (principalmente usadas)**. Cada cliente (loja) recebe:

- Loja virtual própria, com **domínio próprio** e identidade visual configurável.
- Painel administrativo para cadastro de peças, estoque, pedidos e clientes.
- **Estoque único** publicado automaticamente em **Mercado Livre, Instagram (catálogo Meta) e OLX**.
- **Automação por IA**: cadastro de peça a partir de foto, respostas a compradores, preço sugerido, alertas e resumo diário.

Todos os clientes compartilham **o mesmo código**; só a configuração muda por cliente. O primeiro cliente é uma oficina mecânica (piloto), mas **nada pode ser codificado exclusivamente para ela**.

## 3. Stack definida

- **Back-end:** .NET 10, ASP.NET Core Web API, EF Core, Clean Architecture (Domain, Application, Infrastructure, Api).
- **Front-end:** Angular (última versão estável), duas aplicações: `storefront` (loja, com tema por cliente) e `admin` (painel).
- **Banco:** PostgreSQL, com isolamento por `tenant_id` + Row-Level Security.
- **Fila/jobs:** proponha entre RabbitMQ + MassTransit ou fila no próprio PostgreSQL (ex.: Hangfire/outbox). Critério: rodar bem numa única VPS no início e escalar depois.
- **Proxy/SSL:** Caddy com *on-demand TLS* (certificado automático por domínio de cliente, só para domínios cadastrados).
- **IA:** API da Anthropic (Claude) com visão, atrás de uma interface própria para poder trocar de provedor.
- **Infra:** Docker Compose para desenvolvimento e para a VPS de produção; CI no GitHub Actions.

## 4. Requisitos funcionais

### 4.1 Multi-tenant e onboarding
- RF01 — Cadastro de cliente (tenant) com CNPJ, razão social, logo, cores e textos institucionais.
- RF02 — Subdomínio automático na plataforma (`loja.plataforma.com.br`) ao contratar.
- RF03 — Domínio próprio: o cliente informa o domínio, o sistema mostra os registros DNS (CNAME do `www`, A do domínio raiz), verifica a propagação e libera o SSL. Registros MX do cliente nunca são alterados.
- RF04 — Resolução do tenant pelo cabeçalho Host em toda requisição da loja.
- RF05 — Assistente de onboarding em passos: dados da empresa, domínio, gateway, dados fiscais, frete, Mercado Livre, Instagram, OLX.
- RF06 — Cofre de segredos por tenant (chaves de gateway, certificado A1, tokens OAuth), criptografados; nunca em texto aberto no banco nem em logs.
- RF07 — Usuários e perfis por tenant (dono, operador); painel de superadmin da plataforma.

### 4.2 Catálogo e estoque
- RF08 — Cadastro de peça: título, descrição, fotos, estado (nova/usada/recondicionada), preço, quantidade, dimensões e peso, código interno/OEM.
- RF09 — Compatibilidade por veículo (marca, modelo, ano, motorização), usada na busca da loja e nos atributos do Mercado Livre.
- RF10 — Rastreabilidade de peça usada: origem, nota fiscal de entrada, identificação exigida pela Lei 12.977/2014 (campos configuráveis).
- RF11 — Importação inicial por planilha e por importação dos anúncios já existentes no Mercado Livre do cliente.
- RF12 — Estoque com **reserva atômica**: pedido não pago segura a peça por tempo configurável (padrão 30 min).

### 4.3 Loja virtual
- RF13 — Vitrine responsiva com busca por texto, código e veículo.
- RF14 — Carrinho, checkout, cálculo de frete (via plataforma de frete, ex.: Melhor Envio).
- RF15 — Pagamento por gateway configurável por tenant, via interface `IPaymentGateway` (implementações iniciais: Cielo E-commerce e Mercado Pago), confirmado por webhook.
- RF16 — Área do cliente final: pedidos e status.

### 4.4 Pedidos e fiscal
- RF17 — Pedidos unificados de todos os canais (site, Mercado Livre, OLX, balcão) num único painel, com origem identificada.
- RF18 — Lançamento de venda de balcão no painel, com baixa de estoque e sincronização.
- RF19 — Emissão de NF-e via serviço de API fiscal, atrás de interface `IFiscalProvider`.
- RF20 — Cancelamento e devolução devolvem a peça ao estoque e republicam nos canais.

### 4.5 Integrações com canais
Todas atrás de uma interface comum `IChannelConnector` (publicar, atualizar preço/quantidade, pausar, receber eventos).
- RF21 — **Mercado Livre:** um único app no DevCenter para todos os tenants; cada cliente autoriza por OAuth 2.0; renovação automática de tokens; publicação com categoria, atributos e compatibilidade; webhooks de pedidos, pagamentos, perguntas e mensagens (responder HTTP 200 imediatamente e processar via fila).
- RF22 — **Instagram/Meta:** feed de catálogo por tenant (URL de feed e/ou Catalog API), disponibilidade e preço atualizados; produtos levam ao checkout do site.
- RF23 — **OLX:** integração via hub integrador (adapter isolado, pois o fornecedor ainda será definido).
- RF24 — Ao zerar o estoque de uma peça, pausar os anúncios em todos os canais em segundos.

### 4.6 Sincronização
- RF25 — Toda alteração de estoque gera evento na fila (padrão outbox) e é propagada a todos os canais ativos do tenant.
- RF26 — Idempotência: cada evento externo tem chave única; webhooks repetidos não alteram o estoque duas vezes.
- RF27 — Reconciliação horária: compara quantidade de cada anúncio externo com a da API e corrige divergências.
- RF28 — Novas tentativas com backoff e alerta após falhas seguidas.
- RF29 — Log de integração consultável por produto e por pedido no painel.

### 4.7 IA
- RF30 — Cadastro assistido: a partir de fotos + código, gerar título, descrição, categoria/atributos do Mercado Livre e sugestão de compatibilidade; humano revisa antes de publicar.
- RF31 — Respostas a compradores (perguntas do Mercado Livre e mensagens do Instagram) usando só dados do cadastro; automático em perguntas simples, rascunho para revisão nas demais; nunca afirmar compatibilidade ausente no cadastro.
- RF32 — Preço sugerido com base em anúncios semelhantes.
- RF33 — Guardião da sincronização: diagnosticar falhas a partir dos logs e alertar (e-mail/WhatsApp).
- RF34 — Resumo diário por tenant: vendas por canal, peças paradas há mais de 90 dias, perguntas pendentes.
- RF35 — Toda saída da IA registrada para auditoria; IA nunca altera preço, estoque ou cancela pedido por conta própria.
- RF36 — Limites de uso de IA por plano, com contador e bloqueio/pacote excedente.

### 4.8 Assinatura
- RF37 — Planos Essencial, Profissional e Completo, com limites (peças ativas, usuários, cadastros com IA/mês) e recursos habilitados por plano (feature flags).
- RF38 — Cobrança recorrente da assinatura e da taxa de implantação; bloqueio gradual por inadimplência.
- RF39 — Exportação completa dos dados do tenant em caso de cancelamento.

## 5. Requisitos não funcionais

- RNF01 — **Nenhuma consulta pode retornar dados de outro tenant**: filtro global no EF Core + RLS no PostgreSQL + testes automatizados que provem isso.
- RNF02 — Venda dupla de peça única é inaceitável: testes de concorrência obrigatórios na baixa de estoque.
- RNF03 — Propagação de estoque aos canais em até 60 s em operação normal.
- RNF04 — LGPD: minimização de dados de compradores, termos de uso, logs de acesso, exclusão sob pedido.
- RNF05 — Segredos fora do repositório; variáveis de ambiente e cofre.
- RNF06 — Observabilidade: logs estruturados, health checks, métricas da fila e alertas.
- RNF07 — Backup diário do banco e das fotos, fora do servidor.
- RNF08 — Roda numa VPS de 4 vCPU / 8 GB no início; um tenant grande pode ir para banco próprio sem mudar o código.
- RNF09 — Testes: unitários no domínio, integração com PostgreSQL real (Testcontainers), contratos dos adapters com mocks dos canais.
- RNF10 — Código, nomes de entidades e documentação em português do Brasil nos textos de negócio; identificadores de código em inglês.

## 6. Roadmap (ordem de construção)

1. **Núcleo e loja (meses 1–3):** multi-tenant, catálogo, estoque, loja, checkout, frete, NF-e. Marco: piloto vendendo pelo site.
2. **Canais (mês 4):** Mercado Livre e Instagram.
3. **IA e OLX (mês 5):** cadastro assistido e hub OLX.
4. **Produto vendável (mês 6):** onboarding, domínio próprio, assinatura, superadmin, LGPD.
5. **Primeiros clientes (meses 7–8):** IA de atendimento, resumo diário, 3–5 clientes beta.

## 7. O que quero que você produza primeiro (antes de qualquer código de negócio)

1. `CLAUDE.md` na raiz com: visão do produto, stack, convenções, comandos de build/teste e regras inegociáveis (RNF01, RNF02, segredos).
2. `docs/requisitos.md`: os requisitos acima organizados, com **critérios de aceite** para cada RF e as dúvidas em aberto que você identificar.
3. `docs/arquitetura.md`: diagrama (Mermaid) de componentes, fluxo de uma venda no Mercado Livre ponta a ponta, estratégia multi-tenant e de domínio próprio.
4. `docs/adr/`: um ADR por decisão importante (fila, isolamento, provedor de IA, proxy/SSL), com alternativas consideradas.
5. `docs/modelo-dados.md`: modelo de dados (Mermaid ER) com as entidades principais e onde fica o `tenant_id`.
6. `docs/backlog.md`: épicos → histórias → tarefas por etapa do roadmap, com estimativa em horas; e também `docs/backlog.csv` em formato importável no Azure Boards (colunas: Work Item Type, Title, Description, Acceptance Criteria, Parent, Tags, Original Estimate).
7. Estrutura inicial da solução: projetos .NET, apps Angular, `docker-compose.yml` (API, PostgreSQL, fila, Caddy), pipeline de CI e um teste que prove o isolamento entre tenants.

Depois disso, seguimos etapa por etapa do backlog. Ao fim de cada etapa: rode os testes, resuma o que mudou e proponha a próxima.

## 8. Regras de trabalho

- Proponha o plano e espere meu "ok" antes de criar ou alterar muitos arquivos.
- Não invente detalhes de APIs externas: consulte a documentação oficial (Mercado Livre Developers, Meta for Developers, provedor fiscal, gateways) e registre a fonte no ADR ou no código.
- Integrações externas sempre atrás de interfaces, com implementação *fake* para desenvolvimento e testes.
- Commits pequenos e descritivos; nada de segredos no Git.
- Quando faltar informação de negócio, liste a pergunta em `docs/requisitos.md` (seção "Em aberto") e siga com a hipótese mais conservadora, sinalizada.

---

## Mensagem inicial (copie e envie no Claude Code)

```
Leia docs/BRIEF.md inteiro. Ele descreve o produto que vamos construir juntos.
Antes de escrever código, me apresente:
1) seu entendimento do produto em 5 linhas;
2) as dúvidas ou riscos que você vê nos requisitos;
3) o plano para produzir os itens da seção 7, na ordem em que vai fazê-los.
Aguarde meu ok para começar.
```
