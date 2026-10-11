# Manual do usuário

> **Documento vivo.** É atualizado a cada entrega que muda uma tela ou um fluxo, e descreve o que já está no ar. No fim do projeto, passa por revisão para virar o manual oficial.
> Última atualização: 11/10/2026 (RF17: pedidos no painel).

## Sumário

1. [Visão geral](#1-visão-geral)
2. [Endereços de acesso](#2-endereços-de-acesso)
3. [Perfis e permissões](#3-perfis-e-permissões)
4. [Painel da loja](#4-painel-da-loja)
   - 4.1 [Entrar, trocar senha, trocar de loja e sair](#41-entrar-trocar-senha-trocar-de-loja-e-sair)
   - 4.2 [Início](#42-início)
   - 4.3 [Peças](#43-peças)
   - 4.4 [Pedidos](#44-pedidos)
   - 4.5 [Dados da loja](#45-dados-da-loja)
   - 4.6 [Pagamentos](#46-pagamentos)
   - 4.7 [Usuários](#47-usuários)
5. [Loja virtual (o que o comprador vê)](#5-loja-virtual-o-que-o-comprador-vê)
6. [Operação da plataforma](#6-operação-da-plataforma)
7. [Ainda não disponível](#7-ainda-não-disponível)
8. [Histórico do manual](#8-histórico-do-manual)

---

## 1. Visão geral

A plataforma tem duas partes:

| Parte | Quem usa | Para quê |
|---|---|---|
| **Painel da loja** | Dono e operadores da loja | Cadastrar peças, acompanhar e tratar pedidos, configurar a loja, os pagamentos e os usuários |
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
| Pedidos | ✔ | ✔ |
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

**Bloqueio por tentativas:** depois de **5 senhas erradas seguidas**, a conta fica bloqueada por **15 minutos**. O Dono pode desbloquear antes em **Usuários** (seção 4.7).

**No topo do painel:** nome da loja e seu perfil; à direita, seu e-mail, **Trocar de loja** (quando há mais de uma), **Trocar senha** e **Sair**.

A sessão continua aberta por até 7 dias sem uso. Depois disso, é preciso entrar de novo.

### 4.2 Início

Tela de boas-vindas com o nome da loja. Os módulos ficam no menu do topo.

### 4.3 Peças

**Acesso:** menu **Peças** (Dono e Operador).

**Lista de peças**
- Busca por **título, código interno ou código OEM** (espaços, pontos e hífens são ignorados).
- Filtros por **situação** (rascunho, ativa, inativa) e por **veículo** (marca → modelo → ano-modelo).
- Clique na peça para abrir o cadastro. Use **Nova peça** para cadastrar.

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

**Estoque**
- O cadastro mostra três números: **disponível**, **físico** e **reservado**. Reservado é o que está preso em pedidos aguardando pagamento.
- Não é possível baixar a quantidade abaixo do que está reservado.

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
- **Busca**: número do pedido, nome, e-mail ou CPF do comprador.
- **Origem**: hoje, só **Loja virtual**. Mercado Livre, OLX e balcão entram aqui quando estiverem disponíveis.
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

> **Estorno:** o cancelamento **não devolve o dinheiro sozinho**. Faça o estorno no site do gateway (Mercado Pago ou Cielo).
>
> **Pagamento depois do cancelamento:** se o comprador pagar um pedido que a loja cancelou, o pedido vai para **Em análise**, para a loja decidir entre aprovar e cancelar (estornando).

Se duas pessoas tentarem a mesma ação ao mesmo tempo, só a primeira vale. A outra recebe uma mensagem e vê o pedido atualizado.

### 4.5 Dados da loja

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

### 4.6 Pagamentos

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

### 4.7 Usuários

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
- Venda de balcão e emissão de NF-e.
- Frete real (Melhor Envio): hoje o frete é simulado.
- Estorno automático e aviso por e-mail ao comprador.
- Devolução depois de entregue.
- Automações de IA: cadastro por foto, atendimento, preço sugerido, alertas e resumo diário.
- Configuração do prazo de pagamento pelo painel (hoje, 30 minutos).

## 8. Histórico do manual

| Data | Mudança |
|---|---|
| 11/10/2026 | Primeira versão: acesso, perfis, peças, pedidos (RF17), dados da loja, pagamentos, usuários e loja virtual. |
