# Manual do usuário

> **Documento vivo.** É atualizado a cada entrega que muda uma tela ou um fluxo, e descreve o que já está no ar. No fim do projeto, passa por revisão para virar o manual oficial.
> Última atualização: 11/10/2026 (RF10: rastreabilidade da peça usada).

## Sumário

1. [Visão geral](#1-visão-geral)
2. [Endereços de acesso](#2-endereços-de-acesso)
3. [Perfis e permissões](#3-perfis-e-permissões)
4. [Painel da loja](#4-painel-da-loja)
   - 4.1 [Entrar, trocar senha, trocar de loja e sair](#41-entrar-trocar-senha-trocar-de-loja-e-sair)
   - 4.2 [Início](#42-início)
   - 4.3 [Peças](#43-peças)
   - 4.3.1 [Veículos doadores e rastreabilidade](#431-veículos-doadores-e-rastreabilidade)
   - 4.4 [Pedidos](#44-pedidos)
   - 4.5 [Venda de balcão](#45-venda-de-balcão)
   - 4.6 [Dados da loja](#46-dados-da-loja)
   - 4.7 [Pagamentos](#47-pagamentos)
   - 4.8 [Usuários](#48-usuários)
5. [Loja virtual (o que o comprador vê)](#5-loja-virtual-o-que-o-comprador-vê)
6. [Operação da plataforma](#6-operação-da-plataforma)
7. [Ainda não disponível](#7-ainda-não-disponível)
8. [Histórico do manual](#8-histórico-do-manual)

---

## 1. Visão geral

A plataforma tem duas partes:

| Parte | Quem usa | Para quê |
|---|---|---|
| **Painel da loja** | Dono e operadores da loja | Cadastrar peças, lançar vendas de balcão, acompanhar e tratar pedidos, configurar a loja, os pagamentos e os usuários |
| **Loja virtual** | Comprador | Buscar peças, comprar (entrega ou retirada), pagar e acompanhar o pedido |

O **estoque é único**: a mesma quantidade vale para a loja virtual e, quando chegarem as integrações, para os outros canais. Uma peça reservada num pedido deixa de aparecer como disponível para os outros compradores.

## 2. Endereços de acesso

| O quê | Endereço |
|---|---|
| Painel da loja | *a definir para produção*; em desenvolvimento: `http://localhost:4200` |
| Loja virtual | `https://<endereço-da-loja>.<domínio-da-plataforma>` (ex.: `demo.localhost` em desenvolvimento) ou o domínio próprio da loja |
| Acompanhamento do pedido | Link `…/pedido#…` mostrado ao comprador ao confirmar o pedido |

## 3. Perfis e permissões

Cada pessoa tem um perfil **em cada loja** (a mesma pessoa pode ser Dono numa loja e Operador em outra).

| Módulo do painel | Dono | Operador |
|---|:---:|:---:|
| Peças | ✔ | ✔ |
| Veículos doadores e rastreabilidade das peças | ✔ | ✔ |
| Configurar rastreabilidade (modo desmontadora, campos extras) | ✔ | — |
| Pedidos | ✔ | ✔ |
| Venda de balcão | ✔ | ✔ |
| Dados da loja | ✔ | — |
| Pagamentos (chaves dos gateways) | ✔ | — |
| Usuários | ✔ | — |

O menu mostra só os módulos que o perfil pode usar.

## 4. Painel da loja

### 4.1 Entrar, trocar senha, trocar de loja e sair

**Entrar**
1. Abra o endereço do painel e informe **e-mail** e **senha**.
2. No **primeiro acesso** (senha provisória), o painel pede para **criar a sua senha**: de 10 a 128 caracteres.
3. Se você tem acesso a mais de uma loja, escolha a loja na tela **Escolha a loja**.

**Bloqueio por tentativas:** depois de **5 senhas erradas seguidas**, a conta fica bloqueada por **15 minutos**. O Dono pode desbloquear antes em **Usuários** (seção 4.8).

**No topo do painel:** nome da loja e seu perfil; à direita, seu e-mail, **Trocar de loja** (quando há mais de uma), **Trocar senha** e **Sair**.

A sessão continua aberta por até 7 dias sem uso. Depois disso, é preciso entrar de novo.

### 4.2 Início

Tela de boas-vindas com o nome da loja. Os módulos ficam no menu do topo.

### 4.3 Peças

**Acesso:** menu **Peças** (Dono e Operador).

**Lista de peças**
- Busca por **título, código interno ou código OEM** (espaços, pontos e hífens são ignorados).
- Filtros por **situação** (rascunho, ativa, inativa) e por **veículo** (marca → modelo → ano-modelo).
- Clique na peça para abrir o cadastro. Use **Nova peça** para cadastrar uma a uma, ou **Importar planilha** para cadastrar várias de uma vez (veja abaixo).

**Cadastrar uma peça**
1. **Peças → Nova peça**.
2. Preencha:
   - **Código interno**: único na loja. Não muda depois, porque aparece em etiquetas e planilhas.
   - **Título** e **Descrição**.
   - **Estado**: usada, nova ou recondicionada.
   - **Preço (R$)** e **Quantidade em estoque**.
   - **Embalagem**: comprimento, largura, altura (cm) e peso (g). **Sem as quatro medidas, a loja não calcula frete para a peça.**
   - **Códigos OEM**: um por linha.
3. Clique em **Cadastrar**. A peça nasce como **rascunho** e ainda não aparece na loja.

**Fotos**
- Na peça já cadastrada, **Adicionar fotos**: JPG, PNG ou WebP, até 10 MB cada, até 20 fotos.
- A **primeira foto é a capa**. Use ← e → para reordenar e **Remover** para tirar uma foto.
- Uma peça ativa não pode ficar sem foto.

**Compatibilidade (veículos em que a peça serve)**
- Escolha **Marca → Modelo → Versão** e, se quiser, o intervalo de anos (**De/Até**). Depois, clique em **Adicionar**.
- Com os anos em branco, vale a versão inteira.
- Peça sem veículo informado **não aparece na busca por veículo** da loja.

**Publicar ou retirar da loja**
- **Ativar** publica a peça (precisa de ao menos uma foto).
- **Inativar** tira a peça da loja sem apagar o cadastro.

**Importar peças por planilha**

Use para cadastrar o estoque de uma vez, por exemplo na implantação da loja.

1. **Peças → Importar planilha**.
2. **Baixe o modelo** em Excel (.xlsx) ou CSV. Ele vem com o cabeçalho e duas linhas de exemplo; apague os exemplos e coloque uma peça por linha.
   - **Obrigatórias:** código, título, estado (usada, nova ou recondicionada), preço e quantidade.
   - **Opcionais:** descrição, medidas da embalagem (comprimento, largura e altura em cm, peso em g) e códigos OEM (separados por |).
   - O preço pode ser escrito como no Excel em português (por exemplo, 1.234,56). A ordem das colunas não importa, desde que o cabeçalho tenha os nomes do modelo.
3. **Envie o arquivo** (até 5 MB e 5.000 peças). O sistema confere cada linha com as mesmas regras do cadastro manual e mostra uma **prévia**:
   - quantas linhas estão válidas e quantas têm erro, com o **número da linha e o motivo**;
   - quantas têm código que **já existe na loja**: essas serão puladas, sem mudar a peça já cadastrada.
4. Clique em **Importar N peças**. Só as linhas válidas entram. A importação roda em segundo plano, e a tela mostra o **progresso**. Pode sair da tela: em **Importações recentes** dá para acompanhar depois.
5. Ao terminar, as peças estão em **Peças** como **rascunho**. Envie as fotos e ative cada uma para publicá-las na loja.

**Bom saber**
- Para corrigir as linhas com erro, ajuste a planilha e **envie de novo a planilha inteira**. As que já entraram são puladas, então nada é duplicado.
- A importação **não muda** peças que já existem (nem preço, nem estoque). Para mudar uma peça, abra o cadastro dela.
- Compatibilidade com veículos não vem pela planilha: informe no cadastro de cada peça.
- O estoque inicial de cada peça fica registrado como movimento de "importação", em nome de quem enviou a planilha.

**Estoque**
- O cadastro mostra três números: **disponível**, **físico** e **reservado**. Reservado é o que está preso em pedidos aguardando pagamento.
- Não é possível baixar a quantidade abaixo do que está reservado.

### 4.3.1 Veículos doadores e rastreabilidade

**Acesso:** menu **Doadores** (Dono e Operador) e seção **Rastreabilidade** no cadastro de cada peça.

Serve para registrar de onde veio cada peça usada, como pede a Lei 12.977/2014 para empresas de desmontagem. **Esses dados ficam só no painel:** nunca aparecem na loja nem nos canais de venda.

**Cadastrar um veículo doador** (uma vez por carro que entra no desmanche)
1. **Doadores → Novo veículo**.
2. Preencha marca e modelo e informe a **placa** (antiga ou Mercosul) ou o **chassi**. Complete, se tiver:
   - Renavam;
   - nº da certidão de baixa;
   - chave da NF de entrada (44 dígitos);
   - ano-modelo, cor, data de entrada e observações.

   O sistema confere os dígitos do Renavam e da chave da NF, e não aceita duas vezes a mesma placa, chassi ou Renavam.
3. Clique em **Cadastrar**. A lista mostra se a documentação está **Completa** ou **Incompleta**.

**Ligar uma peça ao veículo**
1. Abra a peça em **Peças** e desça até **Rastreabilidade**.
2. Busque o veículo pela placa, chassi ou modelo e clique nele.
3. Informe a **etiqueta de rastreabilidade** (única na loja) e os campos extras que a loja configurou.
4. Clique em **Salvar rastreabilidade**. Dá para buscar a peça pela etiqueta na lista de **Peças**.

O cadastro do veículo mostra todas as peças ligadas a ele. Um veículo só pode ser excluído se não tiver peças.

**Modo desmontadora** (só o Dono, em **Dados da loja → Rastreabilidade de peças usadas**)
- **Desligado (padrão):** os dados de rastreabilidade são opcionais.
- **Ligado:** uma peça **usada** só pode ser **ativada** quando tiver:
  - veículo doador com chassi, Renavam, certidão de baixa e NF de entrada;
  - etiqueta de rastreabilidade;
  - os campos extras marcados como obrigatórios.

  A peça mostra o que falta. Enquanto ela estiver à venda, esses dados não podem ser apagados (inative a peça antes). Peças novas e recondicionadas não são afetadas.
- **Campos extras:** até 10, por exemplo "Nº do registro no Detran". Marque **Obrigatório** para exigi-los no modo desmontadora.

> O sistema guarda os dados para a loja informar ao Detran. O envio ao banco de dados do Detran não é automático.

### 4.4 Pedidos

**Acesso:** menu **Pedidos** (Dono e Operador).

**Situações do pedido**

| Situação | O que significa |
|---|---|
| Aguardando pagamento | Pedido feito; peças reservadas até o prazo de pagamento (por padrão, 30 minutos). Sem pagamento, o pedido é cancelado sozinho e as peças voltam a ficar disponíveis. |
| Em análise | O pagamento chegou, mas a loja precisa decidir: por exemplo, pagou depois de a peça ser vendida, pagou um valor diferente, houve estorno ou o pagamento chegou depois de a loja cancelar. O motivo aparece no pedido. |
| Pago | Pagamento confirmado; peças baixadas do estoque. |
| Enviado | Entregue à transportadora (com ou sem código de rastreio). |
| Entregue | Recebido pelo comprador. |
| Retirado | O comprador retirou as peças no balcão. |
| Cancelado | Pelo prazo de pagamento ou pela loja (com motivo). |

**Encontrar pedidos**
- **Busca**: número do pedido, nome, e-mail, CPF ou CNPJ do comprador.
- **Origem**: **Loja virtual** ou **Balcão**. Mercado Livre e OLX entram aqui quando estiverem disponíveis.
- **Período**: datas **De** e **Até** (dias no horário de Brasília).
- **Situação**: marque uma ou várias.
- **Limpar filtros** volta à lista completa.
- Os filtros ficam no endereço da página: dá para salvar nos favoritos ou mandar o link para outra pessoa da loja.

**Ver o pedido:** clique no **número**. O detalhe mostra:
- situação, datas e quem cancelou;
- pagamentos;
- comprador (nome, CPF, e-mail e telefone);
- entrega ou retirada;
- peças e totais;
- **Histórico**: cada mudança, com data, de → para, quem fez (e-mail do usuário, comprador, pagamento ou sistema) e a observação.

**Tratar o pedido:** os botões aparecem conforme a situação. Cada ação pede **confirmação** antes, com uma explicação do efeito.

| Situação | Ações | O que acontece |
|---|---|---|
| Em análise | **Aprovar pedido** | As peças são baixadas do estoque agora. Se alguma já foi vendida, a aprovação é recusada e a mensagem diz qual peça. |
| Pago, com entrega | **Marcar como enviado** | Código de rastreio opcional. O comprador passa a ver "Enviado" e o rastreio. |
| Enviado | **Marcar como entregue** | Depois disso, o pedido não pode mais ser cancelado. |
| Pago, com retirada | **Marcar como retirado** | Confirme que o comprador retirou as peças. |
| Aguardando pagamento, em análise, pago ou enviado | **Cancelar pedido** | **Motivo obrigatório** (o comprador vê). Peças reservadas são liberadas; peças já baixadas voltam ao estoque. |
| Venda de balcão (retirado) | **Cancelar pedido** | Para corrigir uma venda lançada errado. Motivo obrigatório; as peças voltam ao estoque. Devolva o dinheiro ao cliente, se for o caso. |

> **Estorno:** o cancelamento **não devolve o dinheiro sozinho**. Faça o estorno no site do gateway (Mercado Pago ou Cielo).
>
> **Pagamento depois do cancelamento:** se o comprador pagar um pedido que a loja cancelou, o pedido vai para **Em análise**, para a loja decidir entre aprovar e cancelar (estornando).

Se duas pessoas tentarem a mesma ação ao mesmo tempo, só a primeira vale. A outra recebe uma mensagem e vê o pedido atualizado.

### 4.5 Venda de balcão

**Acesso:** menu **Balcão** (Dono e Operador).

Use para registrar a peça vendida no balcão da loja. A venda baixa o estoque na hora, então a peça deixa de aparecer como disponível na loja virtual, e entra na tela **Pedidos** com origem **Balcão**, já como **Retirado**.

**Lançar uma venda**
1. Em **Peças**, busque por **código interno, título ou OEM** e clique em **Adicionar** na peça vendida. Peças sem estoque disponível não podem ser adicionadas.
2. Ajuste a **quantidade** e, se houve desconto negociado, o **preço** de cada peça. O preço de tabela aparece abaixo quando é alterado.
3. Escolha a **forma de pagamento**: dinheiro, Pix, cartão de débito, cartão de crédito ou outra.
4. **Cliente (opcional)**: nome, CPF ou CNPJ e telefone com DDD. Sem nome, a venda fica como **Consumidor final**. CPF e CNPJ são conferidos.
5. Clique em **Registrar venda**. A tela mostra o número da venda, com link para ela em **Pedidos**.

**Bom saber**
- **Todas as peças ou nenhuma:** se alguma peça acabou nesse meio-tempo (por exemplo, vendida na loja virtual), nada é baixado. A mensagem diz qual peça faltou, e a lista mostra o disponível atualizado.
- **Clique duplo não lança duas vezes:** o mesmo formulário enviado de novo devolve a mesma venda.
- **Peça reservada não sai no balcão:** unidades presas num pedido da loja virtual aguardando pagamento não contam como disponíveis.
- **O histórico da venda** registra quem lançou, a forma de pagamento e os preços ajustados ("de R$ 120,00 por R$ 100,00").
- **Lançou errado?** Em **Pedidos**, abra a venda e use **Cancelar pedido** com o motivo. As peças voltam ao estoque.

### 4.6 Dados da loja

**Acesso:** menu **Dados da loja** (só Dono).

- **Razão social, CNPJ e endereço da loja** aparecem no topo e não mudam por aqui.
- **Logo**: PNG, JPG ou WebP, até 2 MB. Sem logo, a loja mostra o nome no cabeçalho.
- **Nome da loja** e **cores** (principal, fundo e texto). A prévia mostra o resultado, e um aviso aparece se o contraste ficar difícil de ler (dá para salvar mesmo assim).
- **Textos**:
  - **Sobre a loja**: aparece na página inicial.
  - **Trocas e devoluções**: página própria na loja.
  - **Rodapé**: endereço, telefone e horário.
- **Entrega e retirada**:
  - **CEP de onde saem as entregas**: sem CEP, a loja não oferece entrega.
  - **Oferecer retirada no balcão**, com o endereço e o horário que o comprador vê.
- **Ocultar peças sem estoque**: sem marcar, elas aparecem como indisponíveis.
- Clique em **Salvar**. A loja mostra as mudanças em até 1 minuto.

### 4.7 Pagamentos

**Acesso:** menu **Pagamentos** (só Dono).

O dinheiro vai **direto para a conta da loja** no gateway. O comprador paga com cartão ou Pix na página do próprio gateway. As chaves ficam guardadas cifradas e **nunca são mostradas de novo**: o painel exibe só os últimos caracteres.

**Conectar o Mercado Pago**
1. No Mercado Pago, abra **Suas integrações** e crie (ou escolha) uma aplicação.
2. Em **Credenciais de produção**, copie o **Access Token**.
3. Em **Webhooks → Configurar notificações**, informe a URL mostrada no painel, marque o evento **Pagamentos**, salve e copie a **assinatura secreta**.
4. No painel, cole o Access Token e a assinatura e clique em **Salvar**.

**Conectar a Cielo (Checkout Cielo / Link de Pagamento)**
1. No site da Cielo, copie o **MerchantId** (Configurações → Dados cadastrais).
2. Em **E-commerce → Link de Pagamento → Configurações → Credenciais de API**, gere e copie o **Client ID** e o **Client Secret**. Eles aparecem só uma vez.
3. Informe a URL mostrada no painel como URL de notificação e de mudança de status.
4. **Desligue o modo de teste do Checkout** antes de vender.
5. No painel, cole as três chaves e clique em **Salvar**.

**Dois gateways conectados:** a loja cobra pelo **último salvo**. Pagamentos já iniciados no outro continuam sendo confirmados.

**Desconectar:** pagamentos iniciados naquele gateway deixam de ser confirmados automaticamente.

### 4.8 Usuários

**Acesso:** menu **Usuários** (só Dono).

- A lista mostra e-mail, perfil e situação: **Ativo**, **Senha provisória** ou **Bloqueado**.
- **Adicionar usuário**:
  1. Informe e-mail, perfil e uma **senha provisória** (10 a 128 caracteres).
  2. Repasse a senha à pessoa. Ela cria a própria senha no primeiro acesso.
  3. Se o e-mail já tem conta em outra loja, a pessoa só ganha acesso a esta e continua com a senha que já usa.
- **Mudar o perfil**: escolha na coluna **Perfil**.
- **Desbloquear**: libera quem errou a senha 5 vezes, sem esperar os 15 minutos.
- **Remover**: tira o acesso a esta loja (a conta continua nas outras lojas).

## 5. Loja virtual (o que o comprador vê)

**Navegar e buscar**
- **Início**: nome, texto "Sobre a loja" e peças recentes.
- **Busca**: por texto e por veículo (marca, modelo e ano).
- **Página da peça**: fotos, preço, estado, código, quantidade disponível, descrição e veículos em que serve ("Serve em").

**Comprar**
1. Na peça, escolha a quantidade e clique em **Adicionar ao carrinho**.
2. No **Carrinho**, informe o **CEP** e clique em **Calcular frete**. Depois, escolha uma opção de entrega ou a **retirada no balcão**, quando a loja oferece.
3. Clique em **Finalizar compra** e preencha nome, e-mail, telefone, CPF e, na entrega, o endereço.
4. Clique em **Confirmar pedido**. Se preço, estoque ou frete mudaram, a loja avisa e pede para rever.
5. O comprador vai para a **página do pedido**. **Guarde o endereço dessa página: é por ele que se acompanha o pedido.**

**Pagar:** na página do pedido, **Pagar agora** abre a página do gateway. As peças ficam reservadas até o prazo mostrado.

**Acompanhar o pedido**
- Etapas: **Pedido feito → Pago → Enviado → Entregue**, ou **Pedido feito → Pago → Retirado** na retirada.
- Com o pedido enviado, aparece o **código de rastreio**.
- Pedido cancelado mostra o motivo. Pedido em análise mostra que a loja vai entrar em contato.

**Trocas e devoluções:** página com o texto da loja.

## 6. Operação da plataforma

*Feito pela equipe da plataforma, não pela loja.*

- **Criar uma loja e o Dono**: comando `criar-loja` do migrador (ver `CLAUDE.md`, seção Comandos). A senha provisória do Dono aparece na saída do comando e deve ser repassada a ele, que cria a própria senha no primeiro acesso.
- **Tabela de veículos** (marcas, modelos e versões usados na compatibilidade): comando `importar-veiculos` do migrador.

## 7. Ainda não disponível

Itens previstos que **ainda não estão no sistema**:

- Integrações com Mercado Livre, Instagram (catálogo Meta) e OLX.
- Emissão de NF-e (inclusive da venda de balcão).
- Frete real (Melhor Envio): hoje o frete é simulado.
- Compatibilidade com veículos e fotos pela planilha de importação; atualização em massa de peças já cadastradas.
- Importação dos anúncios existentes do Mercado Livre.
- Envio automático da rastreabilidade ao banco de dados do Detran; rastreabilidade pela planilha de importação.
- Estorno automático e aviso por e-mail ao comprador.
- Devolução depois de entregue ou retirado (inclusive no balcão), com opção de não voltar peça avariada ao estoque.
- Automações de IA: cadastro por foto, atendimento, preço sugerido, alertas e resumo diário.
- Configuração do prazo de pagamento pelo painel (hoje, 30 minutos).

## 8. Histórico do manual

| Data | Mudança |
|---|---|
| 11/10/2026 | Primeira versão: acesso, perfis, peças, pedidos (RF17), dados da loja, pagamentos, usuários e loja virtual. |
| 11/10/2026 | Venda de balcão (RF18): nova seção 4.5; origem Balcão, busca por CNPJ e cancelamento da venda de balcão em Pedidos. |
| 11/10/2026 | Importação de peças por planilha (RF11), na seção 4.3 Peças. |
| 11/10/2026 | Veículos doadores, rastreabilidade e modo desmontadora (RF10): seção 4.3.1 e perfis. |
