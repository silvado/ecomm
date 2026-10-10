// Fonte única do backlog. Gera docs/backlog.md, docs/backlog.csv e os itens no Azure Boards.
// Hierarquia Scrum: Epic → Feature → Product Backlog Item → Task. Estimativas em horas.
// PBI: ac = critérios de aceite; t = tarefas [título, horas, estado?] — estado 'doing' ou 'done' (omitido = a fazer).
// O estado de PBI, Feature e Epic é derivado das tarefas (ver progress.mjs); atualize aqui ao avançar e rode azure-sync.

export const epics = [
  {
    title: 'E1 — Núcleo e loja (meses 1–3)',
    tags: 'E1',
    desc: 'Multi-tenant, catálogo, estoque, loja, checkout, frete, NF-e. Marco: piloto vendendo pelo site.',
    features: [
      {
        title: 'Fundação da plataforma',
        pbis: [
          {
            title: 'Estrutura da solução, ambiente local e CI',
            rf: 'RNF09',
            ac: 'Solução .NET (Domain, Application, Infrastructure, Api, Worker) e apps Angular compilam; docker compose sobe API, PostgreSQL, storage S3 (SeaweedFS) e Caddy; CI no GitHub Actions roda build, testes e gitleaks a cada PR.',
            t: [['Criar projetos .NET e referências entre camadas', 4, 'done'], ['Criar apps Angular storefront (SSR) e admin', 6, 'done'], ['docker-compose de desenvolvimento', 4, 'done'], ['Pipeline GitHub Actions (build, testes, gitleaks)', 4, 'done']],
          },
          {
            title: 'Spike: Wolverine + EF Core + RLS',
            rf: 'ADR-0002',
            ac: 'Protótipo publica evento pelo outbox na mesma transação do EF com RLS ativo; latência medida e registrada no ADR-0002.',
            t: [['Configurar Wolverine com persistência PostgreSQL', 4, 'done'], ['Medir latência do outbox e documentar no ADR', 4, 'done']],
          },
          {
            title: 'Isolamento multi-tenant (filtro EF + RLS)',
            rf: 'RNF01',
            ac: 'Teste de integração prova que nenhuma linha de outro tenant é lida ou alterada com filtro EF, sem filtro EF (só RLS) e sem tenant definido; conexão reaproveitada do pool não carrega tenant anterior; build falha se tabela de negócio estiver sem RLS.',
            t: [['Catálogo de tenants com database_key', 6, 'done'], ['Interceptor de conexão (app.tenant_id)', 4, 'done'], ['Migrações: papéis app_user/app_migrator e policies RLS', 6, 'done'], ['Filtro global EF e guarda de TenantId no SaveChanges', 4, 'done'], ['Testes de isolamento (3 cenários + pool)', 6, 'done'], ['Verificação automática de tabelas sem RLS', 3, 'done']],
          },
          {
            title: 'Resolução de tenant pelo Host',
            rf: 'RF04',
            ac: 'Host de subdomínio ou domínio verificado resolve o tenant com cache ≤ 60 s; Host desconhecido → 404; tenant suspenso → página de loja indisponível.',
            t: [['Middleware de resolução com cache', 5, 'done'], ['Página de loja indisponível', 2, 'done'], ['Testes de resolução', 3, 'done']],
          },
          {
            title: 'Observabilidade básica',
            rf: 'RNF06',
            ac: 'Logs JSON com tenant_id e correlation_id e redação de campos sensíveis; /health/live e /health/ready; métricas de fila expostas.',
            t: [['Serilog estruturado com redação', 4], ['Health checks', 2, 'done'], ['Métricas OpenTelemetry da fila', 4]],
          },
          {
            title: 'Cofre de segredos por tenant',
            rf: 'RF06',
            ac: 'Segredos com criptografia envelope; painel mostra só metadados; teste automatizado não encontra segredos em logs; alerta de vencimento do certificado A1.',
            t: [['Serviço de criptografia envelope e rotação de chave mestra', 8, 'done'], ['API de cadastro e metadados', 3], ['Teste de vazamento em logs', 3, 'done'], ['Alerta de vencimento do A1', 3]],
          },
          {
            title: 'Autenticação, usuários e perfis do tenant',
            rf: 'RF07',
            ac: 'Login com e-mail e senha (hash forte), bloqueio após 5 tentativas, perfis Dono e Operador com permissões distintas, limite de usuários do plano respeitado.',
            t: [['Identidade e login no catálogo', 8, 'done'], ['JWT com tenant e papel validados por vínculo', 4, 'done'], ['Políticas de autorização Dono/Operador', 4, 'done'], ['Telas de login e gestão de usuários', 8, 'done']],
          },
          {
            title: 'Cadastro de tenant e subdomínio automático',
            rf: 'RF01, RF02',
            ac: 'Superadmin cria tenant com CNPJ validado e slug único; loja acessível em slug.plataforma.com.br com HTTPS em até 1 min; tema e textos editáveis refletem em até 1 min.',
            t: [['API de tenant e configurações da loja', 4, 'done'], ['Tela de dados da empresa e identidade visual', 6, 'done'], ['Caddy com certificado curinga (DNS-01)', 6]],
          },
        ],
      },
      {
        title: 'Catálogo de peças',
        pbis: [
          {
            title: 'Cadastro de peça com fotos',
            rf: 'RF08',
            ac: 'Peça com todos os campos do RF08; código interno único por tenant; fotos convertidas para WebP em 3 tamanhos no storage com prefixo do tenant.',
            t: [['Domínio e API de peça', 8, 'done'], ['Upload, conversão e storage S3 de fotos', 8, 'done'], ['Telas de listagem e formulário de peça', 16, 'done']],
          },
          {
            title: 'Compatibilidade por veículo',
            rf: 'RF09',
            ac: 'Peça tem 0..n compatibilidades (marca, modelo, anos, motorização) apontando para a tabela global de veículos; busca por veículo retorna só peças compatíveis.',
            t: [['Tabela global de veículos e replicação para ref', 8, 'done'], ['Seleção de compatibilidades no formulário', 8, 'done'], ['Filtro de busca por veículo', 4, 'done']],
          },
          {
            title: 'Rastreabilidade de peça usada',
            rf: 'RF10',
            ac: 'Campos de origem e NF de entrada; campos extras configuráveis; modo desmontagem torna-os obrigatórios para publicar; dados do doador nunca expostos na loja ou canais.',
            t: [['Modelo e regras de rastreabilidade', 6], ['Telas e configuração de campos extras', 4]],
          },
          {
            title: 'Importação de peças por planilha',
            rf: 'RF11',
            ac: 'Modelo de planilha fornecido; validação linha a linha com relatório de erros; importação em background com progresso.',
            t: [['Modelo, leitura e validação da planilha', 8], ['Job de importação com progresso e relatório', 6], ['Tela de importação', 4]],
          },
        ],
      },
      {
        title: 'Estoque e sincronização interna',
        pbis: [
          {
            title: 'Reserva atômica de estoque',
            rf: 'RF12, RNF02',
            ac: 'Reserva e baixa por UPDATE condicional; reserva expira no tempo do tenant (padrão 30 min); 50 requisições simultâneas pela última unidade resultam em exatamente 1 reserva.',
            t: [['Agregado de estoque e comandos SQL atômicos', 6, 'done'], ['Expiração de reserva por mensagem agendada', 4, 'done'], ['Testes de concorrência', 6, 'done']],
          },
          {
            title: 'Outbox, idempotência e retentativas',
            rf: 'RF25, RF26, RF28',
            ac: 'Toda alteração de estoque grava evento na mesma transação; webhook entregue 10 vezes altera o estoque uma vez; falhas retentadas com backoff e enviadas à fila de falhas com alerta após 5 tentativas; reprocessamento pelo painel.',
            t: [['Evento StockChanged pelo outbox', 4, 'done'], ['Inbox de eventos externos com chave única', 6], ['Política de retentativa, fila de falhas e alerta', 4], ['Tela de eventos em falha com reprocessamento', 6]],
          },
          {
            title: 'Log de integração consultável',
            rf: 'RF29',
            ac: 'Toda chamada externa registrada sem segredos; consulta por peça e por pedido no painel; retenção de 90 dias.',
            t: [['Registro de log de integração', 4], ['Telas de consulta por peça e pedido', 6], ['Job de retenção', 2]],
          },
        ],
      },
      {
        title: 'Loja virtual',
        pbis: [
          {
            title: 'Vitrine com SSR, tema por tenant e busca',
            rf: 'RF13',
            ac: 'Páginas renderizadas no servidor com tema do tenant; busca por texto, código e veículo; JSON-LD e Open Graph por peça; Lighthouse mobile ≥ 85 em performance e SEO.',
            t: [['SSR com resolução de tenant e tema via CSS custom properties', 12, 'done'], ['Listagem e busca (texto, código, veículo)', 12, 'done'], ['Página de produto com SEO', 8, 'done']],
          },
          {
            title: 'Carrinho, checkout e frete',
            rf: 'RF14',
            ac: 'Carrinho e checkout como convidado ou com conta; frete calculado pelo Melhor Envio com CEP de origem do tenant; retirada no balcão configurável.',
            t: [['Carrinho', 8, 'done'], ['Checkout', 12, 'doing'], ['IShippingProvider e adapter Melhor Envio', 12, 'doing'], ['Opção de retirada', 2, 'done']],
          },
          {
            title: 'Pagamento por gateway do tenant',
            rf: 'RF15',
            ac: 'Gateway escolhido por tenant; cartão tokenizado no gateway; pedido pago só por webhook validado; webhook repetido sem efeito duplicado; Pix quando o gateway suportar.',
            t: [['IPaymentGateway e fake', 4], ['Adapter Mercado Pago (cartão e Pix)', 16], ['Adapter Cielo E-commerce', 16], ['Webhooks de pagamento com validação e idempotência', 6]],
          },
          {
            title: 'Área do cliente final',
            rf: 'RF16',
            ac: 'Acesso por login ou link mágico; cliente vê só os próprios pedidos daquele tenant, com status e rastreio.',
            t: [['Link mágico por e-mail', 6], ['Lista e detalhe de pedidos', 6]],
          },
        ],
      },
      {
        title: 'Pedidos e fiscal',
        pbis: [
          {
            title: 'Painel de pedidos unificado',
            rf: 'RF17',
            ac: 'Pedidos de todos os canais num painel com origem e filtros; pedido externo único por tenant, canal e id externo.',
            t: [['API de pedidos com filtros', 6], ['Telas de lista e detalhe de pedido', 10]],
          },
          {
            title: 'Venda de balcão',
            rf: 'RF18',
            ac: 'Lançamento de venda com baixa atômica de estoque e evento de sincronização.',
            t: [['Caso de uso e tela de venda de balcão', 8]],
          },
          {
            title: 'Emissão de NF-e',
            rf: 'RF19',
            ac: 'Emissão via IFiscalProvider com certificado do cofre; status, DANFE e XML no pedido; rejeição mostra motivo e permite reenviar; dados fiscais configuráveis por peça com padrão por tenant.',
            t: [['IFiscalProvider e fake', 4], ['Adapter do provedor fiscal escolhido', 20], ['Configuração fiscal de tenant e peça', 6], ['Tela de emissão e acompanhamento', 6]],
          },
          {
            title: 'Cancelamento e devolução',
            rf: 'RF20',
            ac: 'Cancelamento ou devolução retorna quantidades ao estoque e gera evento; opção de não retornar peça avariada; cancelamento de NF-e respeita prazo legal.',
            t: [['Casos de uso de cancelamento e devolução', 6], ['Telas e integração com NF-e de devolução', 4]],
          },
        ],
      },
      {
        title: 'Operação em produção',
        pbis: [
          {
            title: 'Implantação na VPS e backup',
            rf: 'RNF05, RNF07, RNF08',
            ac: 'Compose de produção com segredos por variável de ambiente; backup diário do banco fora do servidor; restauração testada e documentada.',
            t: [['Compose de produção e Caddyfile', 8], ['Backup diário do PostgreSQL e do storage', 8], ['Runbook de implantação e restauração', 4]],
          },
        ],
      },
    ],
  },
  {
    title: 'E2 — Canais (mês 4)',
    tags: 'E2',
    desc: 'Mercado Livre e Instagram (catálogo Meta) sincronizados com o estoque único.',
    features: [
      {
        title: 'Conector de canais comum',
        pbis: [
          {
            title: 'IChannelConnector e propagação de estoque',
            rf: 'RF25, RF24',
            ac: 'Interface comum (publicar, atualizar, pausar, retomar, eventos, estado remoto); handler de StockChanged atualiza todos os canais ativos; peça zerada pausa anúncios em ≤ 60 s; reativa só o que o sistema pausou.',
            t: [['Interface, fake e testes de contrato genéricos', 8], ['Handler de propagação por canal', 8], ['Regras de pausa/reativação e métrica de latência', 6]],
          },
          {
            title: 'Reconciliação horária',
            rf: 'RF27',
            ac: 'Job horário compara quantidade, preço e status dos anúncios com o estoque local, corrige divergências e registra no log, respeitando limites de taxa.',
            t: [['Job de reconciliação por tenant e canal', 12]],
          },
        ],
      },
      {
        title: 'Mercado Livre',
        pbis: [
          {
            title: 'Conexão OAuth do vendedor',
            rf: 'RF21',
            ac: 'Um app para todos os tenants; autorização OAuth por tenant; tokens no cofre; renovação automática; falha marca conexão para reautorizar e alerta.',
            t: [['Fluxo OAuth e armazenamento de tokens', 8], ['Renovação automática e alertas', 4]],
          },
          {
            title: 'Webhooks do Mercado Livre',
            rf: 'RF21, RF26',
            ac: 'Endpoint responde 2xx imediatamente; evento gravado com chave única; tenant resolvido pelo user_id; processamento busca o recurso na API.',
            t: [['Endpoint, inbox e roteamento por vendedor', 8]],
          },
          {
            title: 'Publicação de anúncios com atributos e compatibilidades',
            rf: 'RF21, RF09',
            ac: 'Publica peça com categoria, atributos obrigatórios e compatibilidades criadas pelo vendedor; atualiza preço/quantidade; pausa e reativa.',
            t: [['Escolha de categoria e atributos obrigatórios', 16], ['Envio de compatibilidades', 12], ['Atualização, pausa e reativação', 6]],
          },
          {
            title: 'Pedidos do Mercado Livre no painel',
            rf: 'RF17, RF21',
            ac: 'Venda no ML cria pedido unificado, baixa o estoque atomicamente e propaga; venda sem saldo vira conflito de estoque com alerta.',
            t: [['Processamento de orders_v2 e pagamentos', 12]],
          },
          {
            title: 'Importação dos anúncios existentes',
            rf: 'RF11',
            ac: 'Lista anúncios do vendedor e cria peças vinculadas sem duplicar em reimportação.',
            t: [['Importação de anúncios com vínculo', 12]],
          },
          {
            title: 'Recebimento de perguntas',
            rf: 'RF31',
            ac: 'Perguntas do ML aparecem no painel vinculadas à peça e podem ser respondidas manualmente.',
            t: [['Ingestão e resposta manual de perguntas', 6]],
          },
        ],
      },
      {
        title: 'Instagram / catálogo Meta',
        pbis: [
          {
            title: 'Catálogo Meta por tenant',
            rf: 'RF22, RF24',
            ac: 'Feed por tenant em URL com token; disponibilidade e preço atualizados em segundos pela Catalog Batch API; produto leva à página da peça na loja.',
            t: [['Feed de catálogo', 8], ['Atualizações pela Catalog Batch API', 10], ['Conexão e configuração do catálogo', 6]],
          },
        ],
      },
    ],
  },
  {
    title: 'E3 — IA e OLX (mês 5)',
    tags: 'E3',
    desc: 'Cadastro assistido por IA, preço sugerido e integração OLX via hub.',
    features: [
      {
        title: 'Fundação de IA',
        pbis: [
          {
            title: 'IAiProvider com auditoria',
            rf: 'RF35',
            ac: 'Implementação Anthropic e fake; toda chamada registrada com modelo, tokens, custo, entrada resumida e saída; a camada de IA não tem acesso a comandos de escrita.',
            t: [['IAiProvider, adapter Anthropic e fake', 8], ['Decorador de auditoria', 4], ['Contador de uso por funcionalidade', 4]],
          },
        ],
      },
      {
        title: 'Cadastro assistido e preço',
        pbis: [
          {
            title: 'Cadastro de peça a partir de fotos',
            rf: 'RF30',
            ac: 'Fotos + código geram rascunho com título, descrição, categoria, atributos e compatibilidades sugeridas; campos da IA destacados até revisão; publicação só por humano.',
            t: [['Prompt e saída estruturada validada', 12], ['Tela de revisão do rascunho', 12], ['Conjunto de avaliação de qualidade', 6]],
          },
          {
            title: 'Preço sugerido',
            rf: 'RF32',
            ac: 'Sugere faixa de preço mostrando a amostra usada; nunca altera o preço sozinho.',
            t: [['Coleta de anúncios semelhantes (fonte a validar)', 10], ['Cálculo e tela de sugestão', 6]],
          },
        ],
      },
      {
        title: 'OLX',
        pbis: [
          {
            title: 'Integração OLX via hub',
            rf: 'RF23',
            ac: 'Hub escolhido e documentado em ADR; adapter implementa IChannelConnector e passa nos testes de contrato.',
            t: [['Spike e ADR de escolha do hub', 4], ['Adapter do hub OLX', 24]],
          },
        ],
      },
    ],
  },
  {
    title: 'E4 — Produto vendável (mês 6)',
    tags: 'E4',
    desc: 'Onboarding, domínio próprio, assinatura, superadmin e LGPD.',
    features: [
      {
        title: 'Onboarding e domínio próprio',
        pbis: [
          {
            title: 'Assistente de onboarding',
            rf: 'RF05',
            ac: 'Passos salvos parcialmente e retomáveis; canais aparecem conforme o plano; loja só publica com empresa, gateway e frete concluídos.',
            t: [['Fluxo e estado do onboarding', 8], ['Telas dos passos', 16]],
          },
          {
            title: 'Domínio próprio com SSL automático',
            rf: 'RF03',
            ac: 'Painel mostra registros DNS exatos; verificação a cada 5 min por até 72 h; certificado emitido só para domínio verificado via endpoint ask; MX nunca tocado.',
            t: [['Job de verificação DNS', 10], ['Endpoint ask interno', 4], ['Tela de domínio com instruções', 8]],
          },
        ],
      },
      {
        title: 'Assinatura e planos',
        pbis: [
          {
            title: 'Planos, limites e feature flags',
            rf: 'RF37, RF36',
            ac: 'Planos, limites e recursos são dados; mudança de plano tem efeito imediato; limite de IA bloqueia ou consome pacote excedente.',
            t: [['Modelo de planos e verificação de limites', 12], ['Bloqueio e pacote excedente de IA', 8]],
          },
          {
            title: 'Cobrança recorrente e inadimplência',
            rf: 'RF38',
            ac: 'Cobra mensalidade e implantação pelo gateway da plataforma; bloqueio gradual por inadimplência; pagamento reativa automaticamente.',
            t: [['ISubscriptionBilling e adapter', 20], ['Régua de inadimplência', 10]],
          },
        ],
      },
      {
        title: 'Superadmin e LGPD',
        pbis: [
          {
            title: 'Painel de superadmin',
            rf: 'RF07',
            ac: 'Gestão de tenants e planos; MFA obrigatório; todo acesso a dados de tenant auditado.',
            t: [['Telas de superadmin', 16], ['MFA', 6], ['Auditoria de acesso', 4]],
          },
          {
            title: 'LGPD e exportação de dados',
            rf: 'RNF04, RF39',
            ac: 'Termos e política por tenant; anonimização de cliente sob pedido; log de acesso a dados pessoais; exportação completa em ZIP com link que expira em 7 dias.',
            t: [['Termos e política de privacidade', 6], ['Anonimização de cliente final', 10], ['Log de acesso a dados pessoais', 6], ['Exportação completa do tenant', 12]],
          },
        ],
      },
    ],
  },
  {
    title: 'E5 — Primeiros clientes (meses 7–8)',
    tags: 'E5',
    desc: 'IA de atendimento, resumo diário, guardião da sincronização e 3–5 clientes beta.',
    features: [
      {
        title: 'IA de atendimento e alertas',
        pbis: [
          {
            title: 'Respostas a compradores',
            rf: 'RF31',
            ac: 'Usa só dados do cadastro; classifica em simples (automática se habilitada) ou complexa (rascunho); nunca afirma compatibilidade ausente, verificado por conjunto de avaliação.',
            t: [['Respostas a perguntas do ML', 16], ['Classificação e modo automático', 10], ['Conjunto de avaliação', 10], ['Mensagens do Instagram (após App Review)', 16]],
          },
          {
            title: 'Resumo diário e guardião da sincronização',
            rf: 'RF33, RF34',
            ac: 'Resumo diário no horário do tenant com números de consultas determinísticas; diagnóstico de falhas com ação sugerida enviado por e-mail.',
            t: [['Resumo diário', 12], ['Guardião da sincronização', 12]],
          },
        ],
      },
      {
        title: 'Programa beta',
        pbis: [
          {
            title: 'Implantação de 3 a 5 clientes beta',
            rf: '—',
            ac: 'Clientes beta configurados pelo onboarding sem código específico; feedback registrado no backlog.',
            t: [['Acompanhamento de implantação', 24], ['Correções e ajustes do beta', 16]],
          },
        ],
      },
    ],
  },
];
