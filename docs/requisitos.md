# Requisitos

Origem: [BRIEF.md](BRIEF.md) §4 e §5. Cada RF tem critérios de aceite (CA) verificáveis. Itens marcados **HIPÓTESE** seguem a opção mais conservadora até resposta do dono do produto — ver [Em aberto](#em-aberto).

Legenda de etapa (roadmap §6): **E1** Núcleo e loja · **E2** Canais · **E3** IA e OLX · **E4** Produto vendável · **E5** Primeiros clientes.

**Contexto do piloto (16/10/2026):** o dono do produto e a oficina piloto começam do zero — CNPJ ainda a abrir, sem emissor de NF-e e sem histórico de vendas de peças. Consequências: o modo "emissão externa" (RF19) não tem ferramenta para usar; a integração com a Focus NFe (RF19, parte B) é necessária antes de o piloto ganhar volume, mas só pode ser homologada com CNPJ, inscrição estadual e certificado A1 da loja. Até lá seguem os itens que não dependem disso (cadastro por foto, canais). Perguntas de negócio decorrentes: Q02, Q04, Q45 e Q46.

---

## 1. Multi-tenant e onboarding

### RF01 — Cadastro de tenant · E1 (mínimo) / E4 (completo)
CNPJ, razão social, nome fantasia, logo, cores, textos institucionais. **HIPÓTESE (Q20):** no E1 a loja é criada por comando no servidor (`migrator criar-loja`) e já nasce ativa; CNPJ e razão social não são editáveis pelo painel; o Dono edita nome fantasia, cores e textos.
- CA1: CNPJ é validado (dígitos verificadores) e único na plataforma.
- CA2: logo aceita PNG/JPG/SVG/WebP até 2 MB; SVG é sanitizado antes de servir. **HIPÓTESE (Q21):** no E1 só PNG/JPG/WebP; SVG sanitizado entra depois.
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
- Prova de controle (decidido em 13/10/2026): além do CNAME/A, a loja cria um TXT **só nosso** em `_ecomm-verificacao.<domínio>` com um código aleatório por domínio. Esse nome não é o TXT do domínio (SPF/DKIM/DMARC continuam intocados — CA4) e impede que alguém cadastre um domínio que só aponta para a plataforma sem ser dele. Ver [ADR-0004](adr/0004-proxy-ssl-dominios.md).
- Feito (13/10/2026, antecipado do E4): até um `www` e um domínio raiz por loja, só nos planos com `custom_domain` (RF37); seção "Domínio próprio" em Dados da loja com os registros, situação, "Verificar agora" e "Remover"; job do Worker a cada 5 min (pendentes por 72 h, depois "falhou"; "Verificar agora" reabre a janela); ao verificar, vira o domínio principal (o `www` tem preferência) e a vitrine redireciona com 301 os outros endereços (RF02 CA3); `ask` do Caddy só responde 200 para verificado; DNS atrás de `IDnsResolver` (DnsClient; simulado em dev com `POST /api/dev/dns`). Domínio já usado por outra loja é recusado. Falta: certificado real em produção (VPS, Let's Encrypt).

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
- **Implementado (E4, 16/10/2026):** decisão do usuário: loja nova nasce **em implantação** (`TenantStatus.Onboarding`) e a vitrine mostra "Loja em breve" (503 com tipo próprio) até o Dono publicar; lojas já existentes e a semente de desenvolvimento seguem publicadas. A situação de cada passo vem das configurações já existentes (salvar pela metade = salvar a tela do assunto); só os passos opcionais pulados ficam em `onboarding_skips` (RLS). Publicar é conferido no servidor e invalida o catálogo (abre na hora). O certificado (endpoint `ask`) já vale para loja em implantação. **HIPÓTESES:** empresa concluída = o Dono já salvou Dados da loja (ou a loja personalizada está ligada); fiscal concluído = configuração fiscal salva (vale "não emite"); pagamento simulado (só dev/teste) conta como configurado; domínio próprio também só aparece se o plano incluir; o superadmin não suspende loja em implantação (reativar a publicaria sem os passos); não há "despublicar" pelo painel.

### RF06 — Cofre de segredos por tenant · E1
- CA1: segredos (chaves de gateway, certificado A1 + senha, tokens OAuth) são gravados com criptografia envelope (chave de dados por tenant, protegida por chave mestra fora do banco).
- CA2: nenhum segredo aparece em logs, respostas de API, exportações (RF39) ou mensagens de erro — verificado por teste automatizado que procura valores conhecidos nos logs.
- CA3: o painel mostra apenas metadados (tipo, últimos 4 caracteres, validade); valores nunca são devolvidos ao front.
- CA4: rotação da chave mestra re-encripta as chaves de dados sem downtime.
- CA5: o certificado A1 exibe alerta 30, 15 e 5 dias antes de vencer.
- Feito (13/10/2026): tela **Cofre** (só o Dono) com os metadados dos segredos e o envio do A1 (.pfx + senha, conferidos — senha, chave privada, validade e CNPJ do titular "RAZÃO:CNPJ" igual ao da loja — e guardados cifrados com a validade). Aviso: faixa no painel do Dono a partir de 30 dias (mais forte aos 15 e 5 e depois de vencido) e log de aviso do Worker nos dias 30, 15, 5, no último dia e no primeiro dia depois de vencer. **HIPÓTESE:** e-mail ao Dono fica para quando houver e-mail da plataforma (RF33); com a Focus NFe, o certificado também é cadastrado na Focus (ver RF19 parte B).

### RF07 — Usuários e perfis · E1 (dono/operador) / E4 (superadmin)
- CA1: perfis por tenant: **Dono** (tudo, inclusive cofre, plano e usuários) e **Operador** (catálogo, pedidos, atendimento; sem cofre, plano, usuários, exportação).
- CA2: número de usuários respeita o limite do plano. **HIPÓTESE:** 2 / 5 / 10 (Q17).
- CA3: superadmin da plataforma é um papel separado, com MFA obrigatório; todo acesso de superadmin a dados de um tenant é registrado em log de auditoria. **Implementado (E4, ADR-0007):** conta separada das contas de loja, MFA por app autenticador (TOTP) com 10 códigos de recuperação, sessão de até 8 h (**HIPÓTESE**), superadmin vê só dados do banco `plataforma` (sem peças, pedidos ou clientes) e auditoria somente inclusão. Primeiro superadmin pelo comando `criar-superadmin`.
- CA4: login com e-mail + senha (hash Argon2id/PBKDF2), MFA opcional para Dono; bloqueio após 5 tentativas. **HIPÓTESE:** bloqueio de 15 min, senha de 10+ caracteres, sem recuperação por e-mail por enquanto (Q18, Q19).

## 2. Catálogo e estoque

### RF08 — Cadastro de peça · E1
- CA1: campos: título, descrição, fotos (1–20), estado (nova/usada/recondicionada), preço, quantidade, dimensões (cm) e peso (kg) da embalagem, código interno, código OEM (0..n).
- CA2: código interno é único por tenant.
- CA3: fotos são convertidas para WebP em 3 tamanhos e guardadas no storage com caminho prefixado pelo tenant.
- CA4: peça sem dimensões/peso não pode ser publicada em canal que exija frete calculado.
- **HIPÓTESE (Q22):** código interno em maiúsculas e fixo após o cadastro; título até 120 caracteres (o anúncio do ML terá limite próprio); OEM guardado só com letras e dígitos; quantidade editada no formulário vira ajuste de estoque (nunca abaixo do reservado); peça não é excluída, só inativada.

### RF09 — Compatibilidade por veículo · E1 (loja) / E2 (ML)
- CA1: uma peça tem 0..n compatibilidades (marca, modelo, ano inicial–final, motorização).
- CA2: a tabela de veículos é **da plataforma** (compartilhada, sem `tenant_id`), mantida pelo superadmin e/ou importada da árvore do ML.
- CA3: busca da loja por veículo retorna só peças compatíveis com o veículo escolhido.
- CA4: na publicação no ML, as compatibilidades criadas pelo vendedor são enviadas pela API de compatibilidades (ver [Fontes](#fontes)).
- **HIPÓTESE (Q23, Q24):** tabela de veículos por planilha (fonte completa a definir); compatibilidade com anos opcionais dentro da versão; até 300 por peça; versão que sai da planilha é descontinuada (compatibilidades existentes continuam valendo).

### RF10 — Rastreabilidade de peça usada · E1
- CA1: peça usada tem: origem (veículo doador: placa/chassi parcial, Renavam, certidão de baixa), NF de entrada, e campos extras configuráveis por tenant.
- CA2: se o tenant marcar "empresa de desmontagem (Lei 12.977)", esses campos tornam-se obrigatórios para publicar. **HIPÓTESE:** opcionais por padrão.
- CA3: dados do veículo doador nunca aparecem na loja nem nos canais.
- Decisões (Q34): **veículo doador** cadastrado uma vez (placa antiga ou Mercosul e/ou chassi; Renavam e chave da NF de entrada com dígito verificador conferido; certidão de baixa; marca, modelo, ano, cor, data de entrada) e ligado às peças; placa e chassi completos, só no painel. Na peça: veículo doador, **etiqueta de rastreabilidade** (única na loja) e até 10 **campos extras** configurados pelo Dono. **Modo desmontadora** desligado por padrão; ligado, peça **usada** só vai à venda com o veículo completo (chassi, Renavam, certidão de baixa, NF de entrada), a etiqueta e os campos obrigatórios — e não perde esses dados enquanto estiver à venda. Não há integração com o banco de dados do Detran nesta etapa (a loja informa lá; HIPÓTESE até a Q02).

### RF11 — Importação inicial · E1 (planilha) / E2 (ML)
- CA1: importação por planilha (modelo XLSX/CSV fornecido) valida linha a linha e gera relatório de erros sem importar parcialmente sem aviso.
- CA2: importação do ML lista os anúncios ativos/pausados do vendedor, cria peças vinculadas (`ChannelListing`) e não duplica em reimportação.
- CA3: importação roda em background com progresso visível.
- Decisões (Q33): planilha XLSX ou CSV (modelo nos dois; CSV com ";", "," ou tab, UTF-8 ou Windows-1252), até 5.000 peças e 5 MB; colunas código, título, descrição, estado, preço, quantidade, medidas da embalagem e OEM — sem compatibilidade nesta etapa. Prévia valida linha a linha (mesmas regras do cadastro) e mostra os erros; só as linhas válidas são importadas, depois de o usuário confirmar. Código já cadastrado é pulado e listado no relatório (a peça existente não muda), então reenviar a planilha não duplica. Peças entram como rascunho (precisam de foto para ativar).

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
- CA4: peças sem estoque aparecem como indisponíveis ou ocultas, conforme configuração. Padrão de loja nova: indisponíveis (decisão de 2026-10-09); a loja muda em Dados da loja.

### RF14 — Carrinho, checkout e frete · E1
- CA1: frete calculado por `IShippingProvider` (implementação inicial Melhor Envio) com CEP de origem do tenant e dimensões/peso das peças.
- CA2: opção de retirada no balcão configurável.
- CA3: checkout como convidado ou com conta; coleta só os dados necessários (RNF04).
- **HIPÓTESE (Q25, Q26):** no E1 só checkout como convidado (conta de cliente entra com o RF16); carrinho guardado no navegador, com preço, estoque e frete sempre recalculados no servidor; cotação limitada a 30 por minuto por IP; peça sem medidas de embalagem só sai por retirada; enquanto não houver app do Melhor Envio registrado, frete *fake* em dev.
- **HIPÓTESE (Q27):** checkout coleta nome, e-mail, telefone, CPF e — só na entrega — endereço digitado pelo comprador (sem consulta automática de CEP); dados ficam só no pedido. Pedido nasce "aguardando pagamento" com as peças reservadas pelo prazo da loja (padrão 30 min) e, sem pagamento, é cancelado e as peças voltam; até o RF15 não há forma de pagar. Comprador acompanha por link secreto (sem e-mail de confirmação até haver provedor). 10 pedidos por minuto por IP.

### RF15 — Pagamento · E1
- CA1: gateway escolhido por tenant via `IPaymentGateway`; implementações iniciais Mercado Pago e Cielo E-commerce.
- CA2: dados de cartão nunca passam pelos nossos servidores em claro (tokenização/checkout do gateway).
- CA3: o pedido só vira "pago" por webhook validado (assinatura/consulta de confirmação ao gateway), nunca pelo retorno do navegador.
- CA4: webhooks repetidos não duplicam efeito (RF26).
- CA5: Pix suportado se o gateway oferecer. **HIPÓTESE:** Pix e cartão no escopo E1.
- **HIPÓTESE (Q28):** Mercado Pago primeiro (Cielo depois), pelo Checkout Pro: o comprador paga na página do gateway e volta para a página do pedido. O Dono informa no painel as credenciais da própria conta (Access Token e chave secreta do webhook), guardadas no cofre — sem OAuth por enquanto. Pagamento aprovado depois do prazo da reserva: se as peças ainda estão disponíveis, o pedido vira pago; se alguma já foi vendida, ou se o valor pago difere do total, ou se houver estorno depois de pago, o pedido fica "em análise" para a loja resolver — sem estorno automático. Pedido em análise segura as peças até a loja agir (ações no RF17).
- **HIPÓTESE (Q29):** no Mercado Pago, o frete vai como mais um item da preferência (o total cobrado é a soma dos itens, igual ao do pedido) e a preferência expira no fim da reserva. A chave secreta gerada em "Webhooks → Configurar notificações" também assina os avisos enviados ao `notification_url` da preferência; o horário (`ts`) da assinatura não é usado para recusar avisos atrasados (as reentregas do Mercado Pago podem demorar). Contestação no cartão (`charged_back`) é tratada como estorno; `in_mediation` fica como pendente. Só o formato das chaves é conferido ao salvar — chave errada aparece ao abrir o primeiro pagamento. A confirmar com credenciais de teste.
- **HIPÓTESE (Q30):** Cielo pelo Checkout Cielo / Link de Pagamento (página da Cielo; dados de cartão não passam por nós). O Dono informa MerchantId, Client ID e Client Secret, guardados no cofre, e cadastra a URL de notificação da loja no portal da Cielo (uma por loja). A Cielo não assina os avisos: eles só disparam a consulta autenticada (OAuth), que é o que confirma o pagamento; a URL recebida no aviso JSON nunca é seguida, e o aviso JSON só é aceito com o MerchantId da loja. O número enviado à Cielo é `PED{número do pedido}` (só letras e dígitos, até 20); a consulta não traz o total, então o valor pago é a soma do carrinho enviado (frete como item). O checkout da Cielo não tem validade: pagamento depois do prazo segue a regra da Q28. `Voided` (cancelado pela loja) conta como estorno. Transações do modo de teste do Checkout não se distinguem na consulta: o painel orienta desligar o modo de teste. Com Mercado Pago e Cielo conectados, a loja cobra pelo salvo por último e os webhooks dos dois continuam valendo. Webhooks de pagamento limitados a 120 por minuto por IP. A confirmar com credenciais de teste.

### RF16 — Área do cliente final · E1
- CA1: lista de pedidos com status e rastreio; acesso por login ou link mágico enviado por e-mail.
- CA2: cliente final só vê pedidos do próprio e-mail **e** do tenant da loja acessada.
- E-mail (Q38): dois tipos. **Da plataforma** (convite e recuperação de senha do painel, alerta de falha) sai do domínio da plataforma, custo da plataforma. **Da loja ao comprador** (confirmação de pedido, link de acompanhamento, acesso à área do cliente) sai do **SMTP da própria loja** quando configurado no setup (Google Workspace, Microsoft 365 ou outro; credenciais no cofre) e, sem configuração, do **remetente padrão da plataforma** ("Loja X <nao-responda@…>", resposta para o e-mail da loja), incluído na assinatura. **Decidido (12/10/2026): Amazon SES, região São Paulo (sa-east-1)**, como remetente padrão; tudo por SMTP atrás de `IEmailSender`, então trocar de provedor (ex.: Resend) é só mudar as variáveis `Email__Platform__*` do servidor.
- Decidido (Q39): acesso **só por link no e-mail**, sem senha. O comprador informa o e-mail em "Meus pedidos"; a resposta é sempre a mesma (não revela quem comprou) e o link só sai se o e-mail tem pedidos da loja virtual nesta loja. Link de uso único, válido por 15 minutos; abre uma sessão de **30 dias** naquele navegador (cookie HttpOnly), encerrável em "Sair". Pedidos de balcão não aparecem (não têm e-mail). E-mails ao comprador, pela fila com novas tentativas e log de integração: **link de acesso, pedido recebido (com o link secreto do pedido), pagamento confirmado, pedido enviado (com rastreio) e pedido cancelado**. Configuração do e-mail de envio em Dados da loja, só para o Dono (a senha do SMTP fica no cofre), com envio de teste.
- **HIPÓTESE:** até 10 pedidos de link/abertura de sessão por minuto por IP e até 5 links por e-mail por hora em cada loja. Pelo "Meus pedidos", pedido aguardando pagamento não tem botão de pagar: o comprador paga pelo link do e-mail "Pedido recebido".

### RF40 — Loja personalizada (sob orçamento) · E4
Na contratação, o lojista pode optar por um site próprio personalizado com a loja embutida, cobrado à parte (orçamento). Decisões (Q40):
- CA1: o site personalizado é feito pela equipe da plataforma **dentro da própria vitrine**: tema e páginas exclusivos guardados como dados do tenant (mesmo código para todos, mesmo domínio, SEO e checkout intactos). HTML/CSS do tema só pode ser cadastrado pela equipe da plataforma (superadmin) e é sanitizado; nunca script livre.
- CA2: com a opção ligada, o painel do lojista **não permite alterar a aparência** (logo, cores, textos institucionais, layout): a área aparece como **"Loja personalizada"**, com orientação para pedir mudanças à equipe. A API recusa essas alterações (não basta esconder a tela).
- CA3: peças, preços, estoque, pedidos, frete, pagamento, NF-e, e-mail e usuários continuam com o lojista.
- CA4: ligar e desligar a opção é da plataforma (superadmin/contrato), registrado com data; desligar devolve a edição ao Dono.
- **Antecipado no E1 (trava e selo):** coluna `custom_storefront_since` no tenant; até o painel de superadmin (E4), liga/desliga pelo comando do migrador `loja-personalizada --slug <slug> --ligar|--desligar`. Aparência travada = nome fantasia, logo, cores e textos (sobre, trocas e devoluções, rodapé). O tema e as páginas exclusivos (CA1) seguem no E4.
- **Tema e páginas exclusivos (E4, 16/10/2026):** guardados no banco `plataforma` (`tenant_themes`, `tenant_pages`), pois o superadmin só acessa esse banco (ADR-0007). Limpeza no servidor ao salvar com o HtmlSanitizer, por lista do permitido: HTML de conteúdo sem `style` inline, links só `https`/`mailto:`/`tel:`/relativos; CSS com as propriedades da lista padrão da biblioteca, só `@media`, `url()` só `https`/relativo, `<` escapado. A vitrine aplica só com a opção ligada; o CSS vai aninhado em `.custom-theme`. Liga/desliga e edição pela tela do painel da plataforma, com auditoria. **HIPÓTESES:** carrinho, checkout e Meus pedidos ficam fora do tema (só as cores da loja), para não arriscar a venda; até 20 páginas por loja; sem `@font-face`, `@keyframes` nem `@import` (fontes só do sistema); imagens do tema por endereço `https` (sem envio pela tela).

## 4. Pedidos e fiscal

### RF17 — Pedidos unificados · E1 (site, balcão) / E2 (ML) / E3 (OLX)
- CA1: pedidos de todos os canais num único painel com coluna "origem" e filtros por origem/status/período.
- CA2: pedido externo guarda o id do canal e é único por (tenant, canal, id externo).
- **HIPÓTESE (Q31):** etapas depois de pago — entrega: Pago → Enviado (rastreio opcional) → Entregue; retirada: Pago → Retirado; o comprador vê a etapa e o rastreio na página do pedido. A loja resolve o pedido "em análise" aprovando (baixa as peças que ainda não foram baixadas; sem saldo, recusa) ou cancelando. A loja pode cancelar até antes da entrega/retirada, com motivo obrigatório: reservas são liberadas e peças já baixadas voltam ao estoque (movimento "devolução por cancelamento"); o estorno do pagamento é feito pela loja no gateway. Pagamento que chega depois de a loja cancelar vai para análise (só o cancelado pelo prazo é reativado). Cada mudança fica no histórico do pedido (quem, quando, observação). Devolução depois de entregue, estorno automático e aviso por e-mail ao comprador ficam para depois.

### RF18 — Venda de balcão · E1
- CA1: lançamento com peças, quantidade, forma de pagamento e cliente opcional.
- CA2: dá baixa atômica no estoque e gera evento de sincronização.
- Decisões (Q32): a venda vira pedido de origem "Balcão", já pago e retirado, e aparece no painel de pedidos (RF17); formas de pagamento dinheiro, Pix, cartão de débito, cartão de crédito e outra; preço ajustável por item, com o ajuste registrado no histórico; cliente opcional (nome, CPF ou CNPJ, telefone); a loja pode cancelar com motivo para corrigir lançamento errado, devolvendo as peças ao estoque. Devolução de verdade é o RF20.

### RF19 — NF-e · E1
- CA1: emissão via `IFiscalProvider`, com certificado A1 do cofre; status (processando/autorizada/rejeitada) e DANFE/XML disponíveis no pedido.
- CA2: rejeição mostra o motivo da SEFAZ e permite corrigir e reenviar.
- CA3: dados fiscais por peça (NCM, CFOP, CST/CSOSN, origem) configuráveis com padrão por tenant.
- CA4: **HIPÓTESE:** emissão manual (botão) na E1; automática após pagamento como opção posterior.
- Decisões (Q37): o custo da NF-e é da loja (ela é a emitente, com CNPJ e certificado próprios). **"Traga sua conta"**: a loja conecta a conta que já tem num provedor suportado (primeiro: Focus NFe), com credenciais no cofre (RF06), como nos gateways de pagamento. Emissor não suportado (ERP, emissor próprio): **modo "emissão externa"** — a loja emite fora e informa a chave de 44 dígitos ou anexa o XML no pedido. Plano com "NF-e incluída" (conta da plataforma com vários CNPJs, custo repassado na assinatura) fica como opção futura. **Decidido (12/10/2026): Focus NFe** como primeiro provedor suportado, atrás de `IFiscalProvider` — trocar ou acrescentar provedor é um novo adaptador, sem mudar pedidos nem telas.
- Parte A (com provedor falso; adaptador da Focus na parte B): modo de emissão da loja (desligado, pela plataforma, fora da plataforma), ambiente, regime (Simples Nacional com CSOSN, padrão, ou Regime Normal com CST), UF e padrões (NCM, CFOP dentro/fora do estado, CSOSN/CST, origem) configurados pelo Dono; peça sobrepõe NCM, CSOSN/CST e origem. Emissão manual no pedido pago/enviado/entregue/retirado (Dono e Operador), pela fila, com consulta até a resposta da SEFAZ; XML e DANFE no storage privado; rejeição com motivo e nova emissão (cada tentativa fica no histórico da nota); uma nota ativa por pedido; cancelamento com justificativa (15–255) em até 24 h da autorização (fora disso, orienta NF-e de devolução); modo externo registra a chave (com dígito conferido, inclusive CNPJ alfanumérico) ou o XML, que precisa ser do CNPJ da loja.
- **HIPÓTESES** (confirmar com o contador): **NF-e modelo 55 sempre** — retirada e balcão pedem o endereço (e CPF/CNPJ) do destinatário na hora de emitir; NFC-e fica como opção futura. CFOP da loja conforme a UF do destinatário; consumidor final quando o destinatário tem CPF; venda do site "pela internet", do balcão "presencial". Com a Focus, o certificado A1 fica cadastrado na própria Focus (não no cofre).

### RF20 — Cancelamento e devolução · E1
- CA1: cancelar pedido pago ou registrar devolução devolve as quantidades ao estoque e gera evento (republica/despausa nos canais).
- CA2: devolução de peça usada permite marcar "não retorna ao estoque" (avaria).
- CA3: cancelamento de NF-e respeita o prazo legal; fora dele, orienta NF-e de devolução.
- Decisões (Q35) — parte A, sem NF-e: devolução de pedido **entregue ou retirado** (inclusive balcão), **parcial** por peça e quantidade, podendo haver várias, nunca acima do vendido; cada peça marca se **volta ao estoque** ou é **avariada** (só registrada); motivo (arrependimento, defeito/garantia, peça errada, outro); **reembolso só registrado** (valor e forma — o estorno é feito no gateway ou no caixa); sem prazo travado (a tela mostra há quantos dias foi entregue); pedido fica **Devolvido** quando tudo volta (parcial só no histórico); pedido com devolução não pode mais ser cancelado. A NF-e de devolução (CA3) fica para o RF19.

## 5. Integrações com canais

Todos os conectores implementam `IChannelConnector`: `Publish`, `UpdatePriceAndQuantity`, `Pause`, `Resume`, `HandleEvent`, `GetRemoteState` (para reconciliação).

### RF21 — Mercado Livre · E2
- CA1: um único app no DevCenter; cada tenant autoriza via OAuth 2.0 (authorization code); tokens no cofre.
- CA2: o refresh token é renovado antes de expirar; falha de renovação gera alerta e marca a conexão como "reautorizar".
- CA3: publicação com categoria, atributos obrigatórios da categoria e compatibilidades.
- CA4: webhooks (`orders_v2`, `items`, `questions`, `messages`, `payments`) respondem 2xx imediatamente, gravam o evento bruto e enfileiram; o processamento busca o recurso na API (`GET resource`).
- CA5: a notificação é associada ao tenant pelo `user_id` do vendedor.
- Feito (16/10/2026, parte 1 — [ADR-0009](adr/0009-integracao-mercado-livre.md)): CA1 e CA2.
  - **Conexão:** tela **Canais de venda** (só o Dono) com OAuth (state de uso único e PKCE S256), tokens no cofre e a conta (`user_id`) no mapa da plataforma, base do CA5.
  - **Renovação:** antes de vencer, de hora em hora no Worker e na hora de usar, com trava (o refresh é de uso único).
  - **Recusa do ML:** a conexão fica "autorizar de novo", com faixa de alerta no painel e a sincronização parada.
  - **Desconectar:** revoga a autorização e apaga os tokens.
  - **HIPÓTESE:** uma conta do ML por loja.
  - **Pendente:** CA3 (publicação), pedidos; criar o app no DevCenter e ter um endereço HTTPS público para o retorno (VPS).
- Feito (17/10/2026, parte 6):
  - **Envio:** enviado (com rastreio) e entregue nas vendas do ML, pelo aviso de envio e conferido ao importar a venda; não entregue ou cancelado vai para o histórico.
  - **Avisos perdidos:** recuperados de hora em hora pelo `missed_feeds`.
  - **Pendente:** NF-e da venda; cancelar no ML pelo painel.
- Feito (17/10/2026, parte 5): vendas do ML no painel (RF17 CA2).
  - **Venda paga no ML:** vira pedido unificado, com origem Mercado Livre, baixa atômica das peças e atualização dos outros canais.
  - **Sem saldo:** "conflito de estoque" em análise, sem cancelar sozinho (Q01).
  - **Cancelada no ML:** cancela aqui e devolve as peças.
  - **Pendente:** envio e entrega; NF-e da venda; avisos perdidos.
- Feito (17/10/2026, parte 4): compatibilidades do CA3.
  - **Envio:** os veículos da peça vão ao anúncio como famílias do catálogo de veículos do ML (marca, modelo, ano e, quando a peça não serve em todas as versões do ano, motor).
  - **Quando:** logo depois de publicar e pelo botão **Enviar compatibilidades**.
  - **Fora do catálogo do ML:** o veículo que não for achado com segurança fica de fora e aparece num relatório.
  - **Pendente:** remover do anúncio o veículo tirado da peça; restrições de posição; sugestões do ML.
- Feito (17/10/2026, parte 3): CA3, exceto compatibilidades.
  - **Publicação:** na peça, **Anunciar no Mercado Livre**: categoria sugerida pelo preditor do ML; atributos da categoria com os obrigatórios e pré-preenchimento de condição, número de peça (OEM) e SKU; tipo Clássico ou Premium; garantia.
  - **Envio:** anúncio no modelo User Products (`family_name`), fotos enviadas em JPG, descrição. Recusa do ML mostra o motivo.
  - **Depois de publicado:** o estoque e o preço mandam no anúncio (RF24) e a conferência horária vale também para o ML (RF27).
  - **Pendente:** compatibilidades no anúncio; editar anúncio publicado.
- Feito (17/10/2026, parte 2): CA4 e CA5.
  - **Recebimento:** `POST /api/webhooks/canais/mercado-livre` confere o app (`application_id`) e, se configurada, a lista de IPs do ML; responde 200 na hora e grava no inbox com chave única.
  - **Roteamento:** a loja é achada pelo `user_id` do vendedor.
  - **Processamento:** aviso de `items` de um anúncio da loja pede a conferência dele com o canal. Os demais tópicos (vendas, perguntas, mensagens, pagamentos) ficam registrados e ignorados até as próximas partes.
  - **Pendente:** recuperar avisos perdidos (`missed_feeds`) junto com os pedidos.

### RF22 — Instagram / catálogo Meta · E2
- CA1: feed de catálogo por tenant em URL pública não adivinhável (token), no formato aceito pelo Commerce Manager.
- CA2: preço e disponibilidade atualizados no feed imediatamente; para zerar estoque em segundos (RF24) usa-se a Catalog Batch API (`items_batch`), pois o feed agendado é no máximo horário.
- CA3: o link do produto leva à página da peça na loja do tenant.
- Feito (18/10/2026 — [ADR-0010](adr/0010-catalogo-meta.md)): CA1–CA3.
  - **Conexão:** em **Canais de venda**, com id do catálogo e token de usuário do sistema, conferidos na Graph API; o token vai para o cofre.
  - **Feed:** CSV público num endereço com segredo, com as peças ativas com foto; imagem em JPEG, preço "350.00 BRL", disponibilidade e quantidade pelo saldo, link para a peça.
  - **Atualização na hora:** cada mudança de estoque ou preço vai pela Catalog Batch API (`items_batch` UPDATE). Token recusado deixa a conexão em "autorizar de novo".
  - **HIPÓTESE:** marca = marca do primeiro veículo compatível ou nome da loja.
  - **Pendente:** login da Meta (OAuth); vendas pelo checkout do Instagram/Facebook.

### RF23 — OLX via hub · E3
- CA1: adapter isolado `OlxHubConnector` implementando `IChannelConnector`, com contrato definido após escolha do hub.
- CA2: até a escolha, existe só o fake e os testes de contrato genéricos.

### RF24 — Pausa ao zerar estoque · E2
- CA1: quando o disponível chega a 0, todos os anúncios ativos da peça são pausados em ≤ 60 s (meta: ≤ 10 s) em operação normal.
- CA2: quando volta a ter saldo, os anúncios pausados **pelo sistema** são reativados; os pausados manualmente, não.
- Feito (13/10/2026, base do E2): `IChannelConnector` com testes de contrato genéricos e conector simulado; `channel_connections` e `channel_listings` (RLS); cada `StockChanged` vira uma sincronização por anúncio, que lê o saldo atual (eventos fora de ordem não estragam), pausa ao zerar, reativa o que o sistema pausou, atualiza quantidade e preço e respeita a pausa do lojista (painel: Anúncios na peça). Peça inativa conta como sem saldo. Falha do canal segue a política de novas tentativas (RF28) e o log de integração. Latência medida da mudança de estoque até o canal (histograma OpenTelemetry `Ecommerce.Channels` e `/internal/metricas/canais`; log de aviso acima de 60 s). Conectar uma conta também grava o mapa de roteamento do inbox (`channel_accounts`). Conectores reais e publicação com categoria/atributos: RF21–RF23.

## 6. Sincronização

### RF25 — Eventos via outbox · E1
- CA1: toda alteração de estoque grava, na mesma transação, um registro no outbox.
- CA2: um worker publica os eventos e um handler por canal ativo atualiza o anúncio.
- CA3: falha do worker não perde eventos (são reprocessados ao reiniciar).

### RF26 — Idempotência · E1
- CA1: todo evento externo (webhook) tem chave única (ex.: `ml:{topic}:{resource}:{sent}` ou id do evento do gateway) gravada com restrição `UNIQUE`.
- CA2: o mesmo webhook entregue 10 vezes altera o estoque uma única vez (teste automatizado).
- CA3: handlers internos também são idempotentes (chave por evento do outbox).
- Feito (13/10/2026): inbox de avisos de canais sem loja na URL (`inbox.external_events`, chave única origem + chave), roteamento pela tabela `channel_accounts` do banco da plataforma e entrega no escopo da loja (`external_events`, chave única por loja) antes do processador do canal. CA2 provado com o canal simulado: 10 entregas simultâneas do mesmo aviso → 1 registro e 1 efeito. Conta sem loja → "sem dono" + log crítico. Os processadores reais (ML, Meta, OLX) e o preenchimento de `channel_accounts` pela conexão OAuth entram no E2/E3.

### RF27 — Reconciliação horária · E2
- CA1: job horário compara quantidade/preço/status de cada anúncio externo com o estoque local.
- CA2: divergência é corrigida no canal (o estoque local é a verdade) e registrada no log de integração.
- CA3: o job respeita limites de taxa das APIs (lotes e espera).
- Feito (13/10/2026): o Worker pede, de hora em hora, a reconciliação de cada loja com conta de canal; a loja agenda a conferência de cada anúncio em lotes por canal (`Channels:Reconciliation` — padrão 50 por lote a cada 30 s; os limites reais de cada API entram com o conector). Cada conferência lê o estado remoto e corrige o canal para a verdade local: situação (ativo com saldo, pausado sem saldo, pausa do lojista), quantidade e preço. Cada divergência corrigida vai para o log de integração ("reconciliar anúncio", com o antes → depois); conferência sem divergência só atualiza "conferido em". Anúncio excluído ou encerrado no canal fica em erro até a loja decidir.

### RF28 — Retentativas e alerta · E1
- CA1: falhas transitórias são retentadas com backoff exponencial com jitter (ex.: 10 s, 30 s, 2 min, 10 min, 30 min).
- CA2: após N falhas seguidas (padrão 5) o evento vai para "falhas" e gera alerta ao tenant e à plataforma.
- CA3: eventos em falha podem ser reprocessados pelo painel.
- Decisões (Q36): política da ADR-0002 implementada (10 s, 30 s, 2 min, 10 min e 30 min, com jitter; erro não transitório vai direto para a fila de falhas); a falha é registrada para a loja, com motivo sem segredos; **alerta = faixa no painel + log crítico** (e-mail quando houver provedor — Q12); Dono e Operador veem e reprocessam.

### RF29 — Log de integração · E1
- CA1: cada chamada a canal registra tenant, canal, operação, peça/pedido, status, duração, resumo de erro (sem segredos), correlação.
- CA2: consultável por peça e por pedido no painel; retenção 90 dias.
- Implementação: pagamento (criar cobrança, validar aviso, consultar pagamento) e frete (cotar) passam por decoradores que registram cada chamada fora da transação do negócio; consulta de pagamento feita antes de se saber o pedido é ligada a ele pela correlação. Os canais da E2 usam o mesmo mecanismo.

## 7. IA

### RF30 — Cadastro assistido · E3
- CA1: a partir de 1–10 fotos + código, gera título, descrição, categoria e atributos do ML e sugestão de compatibilidade.
  - Feito (16/10/2026): título, descrição (com as características visíveis), estado, códigos OEM legíveis nas fotos e veículos. O "código" é um texto livre opcional ("o que você já sabe": código, veículo de origem, lado).
  - Veículos: só viram sugestão as versões que existem na tabela da plataforma (RF09), com os anos restritos quando a IA informa; os demais voltam como texto para a pessoa escolher à mão. No máximo 30 versões por sugestão.
  - **Pendente:** categoria e atributos do Mercado Livre — dependem da API do ML e entram com o conector do ML (E2).
- CA2: o resultado é sempre um **rascunho**; publicação exige ação humana. A sugestão só preenche o formulário — nada é gravado até a pessoa salvar, a peça nasce em rascunho e o preço é sempre digitado (RF35).
- CA3: campos sugeridos pela IA aparecem destacados até serem revisados (editar o campo ou clicar em "Revisado"). Veículos sugeridos aparecem marcados para a pessoa desmarcar os que não servem; os mantidos ficam com o selo "IA" na peça.
- CA4: consome 1 unidade do limite mensal do plano (RF36) — só quando a sugestão sai; foto inválida, falha da IA ou resposta inútil não contam.
- CA5: as fotos vão para a IA reduzidas (lado maior de 1568 px), em JPEG e sem metadados (localização do celular não sai da plataforma).

### RF31 — Respostas a compradores · E5
- CA1: responde usando apenas dados do cadastro (título, descrição, atributos, compatibilidade, estoque, políticas da loja).
  - Feito (18/10/2026 — [ADR-0011](adr/0011-respostas-a-compradores.md)) para as perguntas do Mercado Livre: título, estado, disponibilidade, código da loja, códigos OEM, medidas, descrição, veículos compatíveis, retirada no balcão e política de troca e devolução. O preço não vai para a IA.
  - Perguntas de anúncio que não foi publicado pela plataforma ficam sem rascunho, e a loja responde à mão.
- CA2: classifica a pergunta em simples (resposta automática, se o tenant habilitar) ou complexa (rascunho para revisão).
  - A IA classifica, mas a resposta só sai sozinha se passar por regras fixas: nada de compatibilidade, contato (telefone, e-mail, site, WhatsApp), preço, desconto ou pagamento, e no máximo 2.000 caracteres.
  - Rascunho pedido de novo no painel nunca sai sozinho.
  - IA fora do ar não trava a pergunta: fica a nota, e a loja responde à mão.
- CA3: nunca afirma compatibilidade ausente do cadastro; nesse caso, gera rascunho. Verificado por conjunto de testes de avaliação.
  - **HIPÓTESE (conservadora):** toda pergunta sobre veículo vira rascunho, mesmo com o veículo no cadastro. A nota avisa se a peça não tem veículos cadastrados.
  - Conjunto de avaliação: `tests/Application.Tests/Channels/buyer-answer-eval.json`, com 18 casos, incluindo respostas erradas de propósito da IA. Roda na CI.
  - **Pendente:** rodar o mesmo conjunto contra o modelo real, quando houver a chave da Anthropic.
- CA4: **HIPÓTESE:** resposta automática desligada por padrão. Só o Dono liga (Perguntas → opção no topo).

### RF32 — Preço sugerido · E3/E5
- CA1: sugere faixa de preço com base em anúncios semelhantes e mostra a amostra usada.
- CA2: nunca altera o preço; o usuário aceita ou não.
- CA3: **Em aberto:** fonte dos anúncios semelhantes (ver Q09).

### RF33 — Guardião da sincronização · E5
- CA1: analisa falhas recentes do log de integração e gera diagnóstico em linguagem simples com ação sugerida.
  - Feito (18/10/2026 — [ADR-0013](adr/0013-guardiao-da-sincronizacao.md)): os problemas saem de consultas, em três tipos:
    - canal que perdeu a autorização;
    - falha em segundo plano pendente das últimas 24 horas (RF28);
    - a mesma chamada externa com 3 ou mais falhas nas últimas 2 horas, sem acerto depois (RF29).
  - A ação sugerida vem de regras fixas: reconectar, corrigir a peça, reprocessar, aguardar o serviço ou falar com o suporte.
  - A IA só explica o erro em linguagem simples. O texto é recusado se passar de 300 caracteres, trouxer link ou e-mail, ou citar número que não esteja no problema; nesse caso vale um texto fixo.
  - O diagnóstico aparece em **Integrações** (Dono e Operador), sem IA.
- CA2: envia alerta por e-mail; WhatsApp quando houver provedor definido (Q12).
  - Feito (18/10/2026): e-mail aos Donos da loja, conferido a cada 15 minutos. O mesmo problema só é avisado de novo depois de 24 horas, se continuar.
  - Desligado por padrão, inclusive nas lojas existentes (decisão do usuário, 18/10/2026); o Dono liga em Integrações. Só lojas publicadas e não suspensas.
  - WhatsApp segue pendente (Q12).

### RF34 — Resumo diário · E5
- CA1: enviado em horário configurável (padrão 07:00 America/Sao_Paulo) com vendas por canal (ontem), peças sem venda há > 90 dias e perguntas pendentes.
  - Feito (18/10/2026 — [ADR-0012](adr/0012-resumo-diario.md)): e-mail aos Donos da loja, configurado em **Dados da loja → Resumo diário** (liga/desliga, hora cheia, prévia dos números de ontem).
  - **HIPÓTESE:** ligado por padrão, inclusive nas lojas existentes; só lojas publicadas e não suspensas.
  - **Vendas:** pedidos pagos ontem que não estão cancelados nem em análise, por origem (loja virtual, balcão, Mercado Livre).
  - **Peças paradas:** ativas, com saldo, cadastradas há mais de 90 dias e sem baixa por venda nos últimos 90 (lista as 5 mais antigas).
  - **Perguntas:** as sem resposta (RF31), com a data da mais antiga.
  - Sai uma vez por dia. Se o Worker estiver parado no horário, sai assim que voltar, no mesmo dia.
- CA2: os números do resumo vêm de consultas determinísticas; a IA só redige o texto.
  - A IA recebe os números prontos e escreve só a abertura. O texto é descartado se citar qualquer número que não esteja nos fatos (conta, porcentagem, comparação).
  - Os números do e-mail saem sempre das consultas, em tabela. Se a IA estiver fora do ar, o resumo sai sem a abertura.

### RF35 — Auditoria e limites da IA · E3
- CA1: toda chamada registra tenant, usuário, funcionalidade, modelo, tokens, custo estimado, entrada (resumida) e saída.
- CA2: a camada de IA não tem acesso a comandos que alterem preço, estoque ou pedidos — garantido por arquitetura (a IA devolve sugestões; só casos de uso humanos aplicam).
- **Implementado — fundação (E3, 16/10/2026; ADR-0003 complemento):** porta `IAiCompletion` com provedor Anthropic (SDK oficial, desligado sem chave), simulado (dev/testes) e registro de toda chamada em `ai_interactions` (RLS), inclusive falhas. Teste de arquitetura: provedores não recebem banco, fila nem contêiner; o modelo não recebe ferramentas. **HIPÓTESES:** custo em dólares pela tabela de preços configurada; falha ao gravar o registro não descarta a resposta (log crítico); ainda sem tela de consulta do registro (o superadmin e o Dono a terão quando houver funcionalidade de IA em uso).

### RF36 — Limites de uso de IA · E4
- CA1: contador mensal por tenant e por funcionalidade.
- CA2: ao atingir o limite, bloqueia novas chamadas com mensagem clara, ou consome pacote excedente se contratado.
- **Implementado — contador e bloqueio (16/10/2026):** `ai_usage_counters` (RLS) por loja, funcionalidade e mês; reserva atômica antes da chamada (concorrência não passa do limite) e devolução se falhar. Decisão do usuário: **só o cadastro por foto bem-sucedido conta** para o limite do plano (`ai_listings_month`); as outras funcionalidades são contadas, sem limite. Cartão "Seu plano" mostra o uso real. **HIPÓTESE / em aberto:** pacote excedente (Q44).

## 8. Assinatura

### RF37 — Planos e feature flags · E4
- CA1: planos Essencial, Profissional, Completo com limites (peças ativas, usuários, cadastros com IA/mês) e recursos habilitados.
- CA2: limites e recursos são dados (tabela), não código; mudar o plano de um tenant tem efeito imediato.
- Feito (13/10/2026, antecipado do E4): `plan_limits` (usuários, peças ativas, cadastros com IA/mês) e `plan_features` (canais, domínio próprio, automações de IA) no banco da plataforma, lidos a cada consulta (sem cache). Peça acima do limite não é ativada (trava por loja contra ativações simultâneas); canal fora do plano não conecta; cartão "Seu plano" no Início (Dono) com uso e recursos; troca pelo comando `plano --slug <loja> --plano <código>` até a cobrança (RF38). **HIPÓTESE (Q41):** ao baixar de plano, o que já passou do limite continua; só não dá para acrescentar. O bloqueio pelo limite de IA entra com o RF36.

### RF38 — Cobrança recorrente · E4
- CA1: cobra mensalidade e taxa de implantação via gateway **da plataforma**.
- CA2: inadimplência gradual: D+3 aviso; D+10 painel somente leitura; D+20 loja e sincronização suspensas; D+60 tenant marcado para encerramento. **HIPÓTESE** de prazos.
- CA3: pagamento regulariza o acesso automaticamente.
- **Implementado — cobrança (E4, 16/10/2026; ADR-0008):** decisões do usuário: **preço por plano** (editado pelo superadmin; zero = sem cobrança), mensalidade **a partir da publicação** (1ª vence no dia; as seguintes no mesmo dia do mês, até o dia 28) e loja **continua vendendo** na carência (Q13). Tela Assinatura (Dono) com faturas e **Pagar** pelo gateway da plataforma (só o simulado até Q05); webhook com idempotência e baixa pela fila após consultar a cobrança; superadmin lança a taxa de implantação, registra pagamento manual e cancela fatura (auditado). **HIPÓTESES:** fatura gerada 10 dias antes; sem cobrança retroativa; lojas já publicadas começam no dia da primeira rodada do job; cobrança no gateway criada no clique em Pagar e reaproveitada; pago a menor não quita; pago após cancelada conta como paga; uma taxa de implantação por loja.
- **Implementado — régua (E4, 16/10/2026):** o job de hora em hora (e cada pagamento ou cancelamento, na hora) leva a loja ao estágio do atraso da fatura em aberto mais antiga: D+3 faixa no painel; D+10 `ReadOnly` (gravações do painel recusadas; vitrine e canais vendendo — Q13); D+20 `Suspended` (vitrine fora do ar, anúncios pausados nos canais); D+60 `Closing` (só marcada). `tenants.billing_hold` separa o bloqueio da régua (sai sozinho com o pagamento — CA3) da suspensão da equipe (prevalece; só a equipe reativa). Decisão do usuário (16/10/2026): **loja suspensa pausa os anúncios** nos canais (também na suspensão manual) e eles voltam ao reativar. **HIPÓTESES:** prazos (Q13); painel somente leitura também na suspensão manual; Assinatura e Privacidade (LGPD) continuam liberadas; superadmin não reativa loja bloqueada pela régua (registra o pagamento ou cancela a fatura); D+60 não apaga nada; outros processos veem a mudança em até 60 s (cache do catálogo).

### RF39 — Exportação de dados · E4
- CA1: o Dono gera um pacote (ZIP) com peças, fotos, pedidos, clientes e notas em CSV/JSON + arquivos.
- CA2: o link de download expira em 7 dias e o acesso é registrado.
- CA3: segredos do cofre não são exportados.
- **Implementado (E4):** tela Privacidade (Dono); pacote montado na fila em armazenamento privado (CSV com `;` e decimais com vírgula, fotos originais, XML/DANFE); uma exportação por vez; download pelo painel, registrado; vencida, o arquivo é apagado na retenção diária. Pendente há mais de 2 h vira falha (**HIPÓTESE**).

---

## Requisitos não funcionais

| Id | Requisito | Critério de aceite |
|---|---|---|
| RNF01 | Isolamento total entre tenants | Teste de integração prova que (a) com filtro EF, (b) **sem** filtro EF (só RLS) e (c) sem `app.tenant_id` definido, nenhuma linha de outro tenant é retornada ou alterada. Teste roda no CI. |
| RNF02 | Sem venda dupla | Teste de concorrência (N threads, última unidade) → exatamente 1 sucesso, saldo nunca negativo (`CHECK (disponivel >= 0)`). |
| RNF03 | Propagação ≤ 60 s | Métrica "idade do evento no outbox ao ser concluído" p95 < 60 s; alerta se p95 > 60 s por 5 min. |
| RNF04 | LGPD | Política de privacidade e termos por tenant; coleta mínima; exclusão/anonimização de cliente final sob pedido em ≤ 15 dias; log de acesso a dados pessoais. **Implementado (E4):** textos por loja com modelo padrão (Q42); anonimização pelo Dono na hora (Q43); registro somente inclusão de abertura de pedido, download de nota, exportação e anonimização. |
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
| Q02 | O piloto é empresa de desmontagem credenciada no DETRAN (Lei 12.977) ou só revende? Em qual UF? (16/10/2026: negócio novo, ainda a constituir — a origem das peças, desmonte próprio ou compra de terceiros com nota, define o credenciamento.) | Campos de rastreabilidade opcionais, com modo "desmontagem" que os torna obrigatórios. | RF10 |
| Q03 | Provedor fiscal (Focus NFe, PlugNotas, eNotas, NFE.io…)? | Só `IFiscalProvider` + fake até a escolha; ADR quando definido. | RF19 |
| Q04 | Regime tributário do piloto e tributação da peça usada (CSOSN/CST, CFOP) — validar com contador. (16/10/2026: CNPJ ainda a abrir — MEI ou Simples Nacional? A atividade de peças usadas cabe no MEI? MEI muda a obrigação de emitir NF-e e o limite de faturamento; o sistema supõe NF-e modelo 55 com inscrição estadual.) | Configurável por tenant e por peça; sem padrão "inteligente". | RF19 |
| Q05 | Gateway da plataforma para cobrar a assinatura (Asaas, Iugu, Mercado Pago assinaturas…)? | Interface `ISubscriptionBilling` + fake (implementado em 16/10/2026, ADR-0008). Sem gateway, a equipe registra os pagamentos à mão. | RF38 |
| Q06 | Pix entra no E1? | Sim, via gateway do tenant. | RF15 |
| Q07 | Hub OLX? | Adapter fake. | RF23 |
| Q08 | Mensagens do Instagram (RF31) exigem App Review da Meta para atender vários negócios (Advanced Access). Aceitamos o prazo incerto? | RF31 inicia só com perguntas do ML; Instagram após aprovação. | RF31 |
| Q09 | Fonte de "anúncios semelhantes" para o preço sugerido — a API de busca pública do ML pode ter restrições. | Usar histórico do próprio tenant + itens retornados por API autorizada; validar na E3. | RF32 |
| Q10 | Domínio raiz: o provedor DNS do cliente suporta ALIAS/ANAME/CNAME flattening? | Recomendar redirecionar raiz → `www`; registro A só como alternativa, com IP estável (IP flutuante). | RF03 |
| Q11 | Nome/domínio da plataforma (`plataforma.com.br` é provisório). | Placeholder configurável. | RF02 |
| Q12 | Provedor de WhatsApp para alertas (API oficial Cloud API ou parceiro)? | Só e-mail na primeira versão. | RF33 |
| Q13 | Prazos de inadimplência e se a loja deve continuar vendendo durante a carência. | Decidido em 16/10/2026: a loja **continua vendendo** até a suspensão (D+20). Prazos: ver RF38 CA2 (ainda HIPÓTESE). | RF38 |
| Q14 | Uma peça pode ter mais de 1 unidade (peças novas) ou usadas são sempre quantidade 1? | Quantidade genérica (≥ 0) para todas. | RF08, RF12 |
| Q15 | Variações (lado esquerdo/direito, cor) como produtos distintos? | Sem variações: cada variação é uma peça. | RF08 |
| Q16 | Transportadoras além do Melhor Envio; Mercado Envios para vendas do ML é gerido pelo ML? | Melhor Envio no site; envio das vendas do ML fica com o ML. | RF14 |
| Q17 | Quantos usuários cada plano permite? | Essencial 2, Profissional 5, Completo 10 (tabela `plan_limits`, muda sem deploy). Loja nova começa no Essencial até existir assinatura (E4). | RF07, RF37 |
| Q18 | Regras de bloqueio e senha do painel. | Bloqueio de 15 min após 5 senhas erradas seguidas (o Dono pode desbloquear); 10 tentativas de login por minuto por IP; senha de 10 a 128 caracteres, sem regras de composição; sessão expira após 7 dias sem uso. | RF07 |
| Q19 | Como o lojista recupera a senha esquecida? Não há provedor de e-mail ainda. | O Dono cadastra usuários com senha provisória (troca obrigatória no primeiro acesso) e pode removê-los e cadastrá-los de novo. Recuperação por e-mail entra quando houver provedor (RF33/E4). | RF07 |
| Q20 | Quem cria lojas antes do painel de superadmin (E4)? A loja nasce ativa? Quem pode mudar CNPJ/razão social? | Comando no servidor (`migrator criar-loja`), sem superadmin web (evita MFA antes do E4); loja nasce ativa; CNPJ e razão social só mudam por suporte. | RF01, RF02, RF07 |
| Q21 | Logo em SVG no E1? | Não: só PNG/JPG/WebP até 2 MB. SVG exige sanitização dedicada e entra depois. | RF01 |
| Q22 | Regras do cadastro de peça: código interno editável? Exclusão de peça? Tamanho do título? | Código em maiúsculas, fixo após o cadastro; sem exclusão (só inativar, preservando histórico); título até 120 caracteres; quantidade do formulário = ajuste de estoque, nunca abaixo do reservado. | RF08 |
| Q23 | Fonte da tabela completa de veículos (marcas, modelos, motorizações, anos)? | Planilha CSV importada pelo comando `migrator importar-veiculos`; hoje só a amostra de dev (`deploy/veiculos/amostra.csv`, anos aproximados). Fonte definitiva a decidir: compra/licença de base ou árvore do ML (E2). | RF09 |
| Q24 | Limite de veículos compatíveis por peça? | 300 por peça (evita "peça universal" que polui a busca). | RF09 |
| Q25 | Checkout com conta de cliente no E1? Onde fica o carrinho? | Só convidado no E1 (conta com o RF16); carrinho no `localStorage` do navegador, até 50 peças diferentes, conferido no servidor a cada visita. | RF14, RF16 |
| Q26 | Quando registrar o app da plataforma no Melhor Envio (nome, e-mail técnico, URL de retorno)? | Até lá, provedor de frete *fake* em dev e testes; adaptador do ME testado contra servidor simulado; conexão OAuth da loja no painel entra quando o app existir. | RF14 |
| Q27 | Que dados o checkout coleta, onde ficam e o que acontece com pedido sem pagamento antes do gateway (RF15)? | Nome, e-mail, telefone, CPF (NF-e e etiqueta) e endereço só na entrega, gravados no pedido (cadastro de cliente com o RF16); sem pagamento no prazo da reserva, o pedido é cancelado sozinho — sem confirmação manual. Link secreto de acompanhamento; e-mail de confirmação quando houver provedor (RF33/E4). | RF14, RF15, RF16 |
| Q28 | Como tratar pagamento que chega depois do prazo, com valor diferente ou estornado? Qual gateway e fluxo primeiro? | Mercado Pago, Checkout Pro (redirecionamento), credenciais coladas pelo Dono. Pago tarde com peças disponíveis → pago; peça já vendida, valor diferente ou estorno → "em análise", sem estorno automático (mesma linha da Q01). | RF15, RF12 |
| Q29 | Detalhes da integração com o Mercado Pago a confirmar com credenciais de teste (frete na preferência, assinatura dos avisos do `notification_url`, contestação). | HIPÓTESE: frete como item; mesma chave secreta assina os avisos; sem janela de tempo no `ts`; `charged_back` = estorno (pedido pago vai para análise). | RF15 |
| Q30 | Detalhes da integração com a Cielo a confirmar com credenciais de teste (avisos sem assinatura, número do pedido, valor pago, modo de teste, dois gateways conectados). | HIPÓTESE: aviso só dispara a consulta autenticada; `PED{número}`; valor = soma do carrinho; orientar desligar o modo de teste; cobra pelo último salvo. | RF15 |
| Q31 | O que a loja faz com o pedido depois de pago ou em análise? Pode cancelar pedido pago? | Etapas Enviado (rastreio)/Entregue e Retirado; análise: aprovar ou cancelar; cancelar até antes da entrega, com motivo, devolvendo as peças ao estoque; estorno feito pela loja no gateway. | RF17, RF15, RF12 |
| Q32 | Como é a venda de balcão: em que situação nasce, o preço pode mudar, que dados do cliente? | Vira pedido de origem "Balcão" já pago e retirado (baixa na hora); a loja pode cancelar com motivo para corrigir lançamento errado (peças voltam ao estoque). Preço ajustável por item (desconto negociado, registrado no histórico). Cliente opcional: nome, CPF ou CNPJ (validado) e telefone; sem nome, "Consumidor final". | RF18, RF17 |
| Q33 | Como funciona a importação de peças por planilha? | XLSX e CSV; prévia com relatório de erros e confirmação antes de importar as linhas válidas; código já cadastrado é pulado e avisado (não atualiza a peça); sem compatibilidade com veículos nesta etapa; peças entram como rascunho. | RF11 |
| Q34 | Como organizar a rastreabilidade da peça usada (RF10)? | Cadastro de veículo doador ligado às peças; placa e chassi completos, só no painel; modo desmontadora desligado por padrão (a loja liga); etiqueta e campos extras na peça. | RF10 |
| Q35 | Como registrar devoluções (RF20)? | Parcial por peça e quantidade; avariada não volta ao estoque; 'Devolvido' só quando tudo volta; reembolso apenas registrado; sem trava de prazo, mostrando os dias desde a entrega. | RF20 |
| Q36 | Como alertar falhas de integração e quem as vê (RF28/RF29/RNF06)? | Faixa no painel + log crítico (e-mail depois); Dono e Operador veem o log e reprocessam; métricas da fila num endpoint interno (sem Prometheus por ora). | RF28, RF29, RNF06 |
| Q37 | Quem paga a NF-e e como a loja que já emite usa a plataforma? | Custo da loja; "traga sua conta" com provedor suportado (Focus NFe primeiro) e modo "emissão externa" (chave/XML informados) para os demais; "NF-e incluída" no plano fica para depois. | RF19, RF20 |
| Q38 | De onde saem os e-mails e quem paga? | Da plataforma: domínio e custo da plataforma. Da loja ao comprador: SMTP da loja (Workspace, 365, outro) se configurado; senão remetente padrão da plataforma, incluído na assinatura. Provedor padrão a escolher (SES São Paulo ou Resend), via SMTP. | RF16, RF33, RF07 |
| Q39 | Como o comprador entra na área do cliente e que e-mails recebe? | Só por link no e-mail (sem senha); sessão de 30 dias; e-mails: link de acesso, pedido recebido, pagamento confirmado, pedido enviado (com rastreio) e pedido cancelado. | RF16 |
| Q40 | Como funciona a opção de site personalizado contratada à parte? | Cobrada por orçamento; a equipe da plataforma cria tema e páginas exclusivos na própria vitrine (dados do tenant, mesmo código); o lojista perde só a edição da aparência (logo, cores, textos, layout), que aparece como "Loja personalizada"; peças, preços, estoque, pedidos, frete e pagamento continuam com ele. | RF40, RF01, RF37 |
| Q41 | Quais limites e recursos tem cada plano? | **HIPÓTESE** aprovada como ponto de partida (13/10/2026): usuários 2/5/10; peças ativas 500/3.000/sem limite; cadastros com IA 50/300/1.000 por mês; Mercado Livre e IA de cadastro em todos; Instagram, domínio próprio e demais IAs a partir do Profissional; OLX só no Completo; loja virtual, balcão, NF-e e e-mail em todos. Ajustável por migração de dados quando houver tabela de preços. | RF37, RF36, RF07 |
| Q42 | Quem escreve a política de privacidade e os termos de cada loja? | **HIPÓTESE:** a loja (controladora) escreve; enquanto não escreve, a vitrine mostra um modelo genérico da plataforma com razão social e CNPJ, a revisar por advogado antes do lançamento. Os textos ficam fora da trava da loja personalizada. | RNF04 |
| Q43 | Até onde vai a anonimização do comprador? | Aprovado (15/10/2026): saem e-mail, telefone, endereço de entrega (ficam cidade/UF) e o acesso a Meus pedidos; sem NF-e, também nome e CPF/CNPJ; com NF-e, nome e CPF/CNPJ ficam (guarda fiscal de 5 anos) e a nota não muda. **HIPÓTESE:** pedido em andamento impede; observações livres não são varridas; o registro de acesso não tem prazo de retenção definido. | RNF04 |
| Q44 | Haverá pacote excedente de IA (comprar mais cadastros no mês)? Preço e como cobra (fatura avulsa da assinatura?). | Sem pacote: no limite, bloqueia até o mês seguinte ou troca de plano. | RF36, RF38 |
| Q45 | Reabre Q37 (16/10/2026): as lojas-alvo são pequenas e novas, muitas sem emissor. Oferecer "NF-e incluída" (a plataforma tem uma conta na Focus e cadastra o CNPJ e o A1 de cada loja; custo no plano) já no lançamento, além do "traga sua conta"? A confirmar com a Focus: plano de revenda/parceiro, cadastro de empresas pela API e preço por CNPJ. | Mantida a Q37 ("traga sua conta") até a resposta. O adaptador é o mesmo nos dois modelos: muda só de quem é a credencial. | RF19, RF37, RF38 |
| Q46 | A empresa da plataforma emitirá NFS-e das mensalidades cobradas das lojas? Por qual emissor (a Focus também emite NFS-e)? | Fora do sistema até a resposta: a cobrança da assinatura (RF38) não emite nota. | RF38 |

## Fontes

- Mercado Livre — Compatibilidades de autopeças: https://developers.mercadolivre.com.br/pt-br/compatibilidades-itens-e-produtos-de-autopecas (mudanças em 15/07/2026: compatibilidades vindas do catálogo ficam só para leitura resumida via API; as criadas pelo vendedor seguem gerenciáveis).
- Mercado Livre — Notificações: https://developers.mercadolibre.com.ar/en_us/products-receive-notifications (`orders_v2` recomendado; notificação traz `resource` a ser consultado).
- Meta — Catalog Batch API: https://developers.facebook.com/docs/marketing-api/catalog-batch (recomendada para atualizações mais frequentes que 1 h).
- Meta — App Review Instagram: https://developers.facebook.com/docs/instagram-platform/app-review
- Lei 12.977/2014: https://legis.senado.leg.br/norma/584788/publicacao/15612419 ; Resolução CONTRAN 611/2016.
