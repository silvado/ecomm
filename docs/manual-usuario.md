# Manual do usuário

> **Documento vivo.** É atualizado a cada entrega que muda uma tela ou um fluxo, e descreve o que já está no ar. No fim do projeto, passa por revisão para virar o manual oficial.
> Última atualização: 16/10/2026 (canais de venda: conexão do Mercado Livre).

## Sumário

1. [Visão geral](#1-visão-geral)
2. [Endereços de acesso](#2-endereços-de-acesso)
3. [Perfis e permissões](#3-perfis-e-permissões)
4. [Painel da loja](#4-painel-da-loja)
   - 4.1 [Entrar, trocar senha, trocar de loja e sair](#41-entrar-trocar-senha-trocar-de-loja-e-sair)
   - 4.2 [Início](#42-início)
   - 4.2.1 [Primeiros passos](#421-primeiros-passos)
   - 4.3 [Peças](#43-peças)
   - 4.3.1 [Veículos doadores e rastreabilidade](#431-veículos-doadores-e-rastreabilidade)
   - 4.4 [Pedidos](#44-pedidos)
   - 4.5 [Venda de balcão](#45-venda-de-balcão)
   - 4.5.1 [Integrações](#451-integrações)
   - 4.6 [Dados da loja](#46-dados-da-loja)
   - 4.6.1 [Canais de venda](#461-canais-de-venda)
   - 4.7 [Pagamentos](#47-pagamentos)
   - 4.7.1 [Cofre e certificado digital](#471-cofre-e-certificado-digital)
   - 4.8 [Usuários](#48-usuários)
   - 4.9 [Privacidade (LGPD) e exportação](#49-privacidade-lgpd-e-exportação)
   - 4.10 [Assinatura](#410-assinatura)
5. [Loja virtual (o que o comprador vê)](#5-loja-virtual-o-que-o-comprador-vê)
6. [Painel da plataforma (superadmin)](#6-painel-da-plataforma-superadmin)
7. [Ainda não disponível](#7-ainda-não-disponível)
8. [Histórico do manual](#8-histórico-do-manual)

---

## 1. Visão geral

A plataforma tem duas partes:

| Parte | Quem usa | Para quê |
|---|---|---|
| **Painel da loja** | Dono e operadores da loja | Cadastrar peças, lançar vendas de balcão, acompanhar e tratar pedidos, configurar a loja, os pagamentos e os usuários |
| **Loja virtual** | Comprador | Buscar peças, comprar (entrega ou retirada), pagar e acompanhar o pedido |
| **Painel da plataforma** | Equipe da plataforma (superadmin) | Criar lojas, trocar plano, suspender e reativar, ligar a loja personalizada e consultar a auditoria |

O **estoque é único**: a mesma quantidade vale para a loja virtual e, quando chegarem as integrações, para os outros canais. Uma peça reservada num pedido deixa de aparecer como disponível para os outros compradores.

## 2. Endereços de acesso

| O quê | Endereço |
|---|---|
| Painel da loja | *a definir para produção*; em desenvolvimento: `http://localhost:4200` |
| Loja virtual | `https://<endereço-da-loja>.<domínio-da-plataforma>` (ex.: `demo.localhost` em desenvolvimento) ou o domínio próprio da loja |
| Painel da plataforma | Mesmo endereço do painel da loja, em `/plataforma` (em desenvolvimento: `http://localhost:4200/plataforma`) |
| Acompanhamento do pedido | Link `…/pedido#…` mostrado ao comprador ao confirmar o pedido |

## 3. Perfis e permissões

Cada pessoa tem um perfil **em cada loja** (a mesma pessoa pode ser Dono numa loja e Operador em outra).

| Módulo do painel | Dono | Operador |
|---|:---:|:---:|
| Peças | ✔ | ✔ |
| Veículos doadores e rastreabilidade das peças | ✔ | ✔ |
| Configurar rastreabilidade (modo desmontadora, campos extras) | ✔ | — |
| Pedidos | ✔ | ✔ |
| Emitir, cancelar e registrar NF-e no pedido; dados fiscais da peça | ✔ | ✔ |
| Configurar a nota fiscal da loja (Dados da loja) | ✔ | — |
| Venda de balcão | ✔ | ✔ |
| Integrações (log e reprocessar falhas) | ✔ | ✔ |
| Primeiros passos e publicar a loja | ✔ | — |
| Dados da loja | ✔ | — |
| Canais de venda (conectar o Mercado Livre) | ✔ | — |
| Pagamentos (chaves dos gateways) | ✔ | — |
| Cofre (certificado digital A1 e chaves guardadas) | ✔ | — |
| Usuários | ✔ | — |
| Privacidade: apagar dados de comprador, registro de acesso e exportação | ✔ | — |
| Privacidade e termos da loja (Dados da loja) | ✔ | — |
| Assinatura (mensalidade e faturas da plataforma) | ✔ | — |

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

**Seu plano** (só o Dono)
- Mostra o plano da loja (Essencial, Profissional ou Completo) e quanto de cada limite já foi usado: peças ativas, usuários e cadastros com IA no mês.
- Também lista os recursos incluídos, por exemplo Mercado Livre, Instagram e domínio próprio. Loja virtual, balcão, nota fiscal e e-mails estão em todos os planos.
- Quando a loja chega a um limite, aparece um aviso. Para mudar de plano, fale com a equipe da plataforma.

**Limites do plano**
- **Peças ativas:** no limite, ativar uma peça nova é recusado com a mensagem do plano. Inative outra peça ou mude de plano. Peças em rascunho ou inativas não contam.
- **Usuários:** no limite, não dá para adicionar outro usuário.
- **Canais:** conectar um canal que o plano não inclui é recusado.
- **Cadastros com IA no mês:** conta cada cadastro por foto que a IA concluiu (tentativas que falharam não contam). No limite, a IA recusa novos cadastros até o mês seguinte ou a mudança de plano; o cadastro manual continua normal. As outras ajudas da IA (quando chegarem) não têm limite por enquanto.
- Ao mudar para um plano menor, nada é desativado: só não dá para acrescentar além do novo limite.

**Sua loja ainda não está no ar** (só o Dono, enquanto a loja não foi publicada): mostra quantos passos já foram concluídos e leva aos **Primeiros passos**.

### 4.2.1 Primeiros passos

Toda loja nova começa **em implantação**: a loja virtual mostra **"Loja em breve"** e não vende até o Dono publicar. Em **Primeiros passos** (menu do topo, só o Dono) aparece cada passo e a situação dele:

| Passo | Onde se configura | |
|---|---|---|
| **Dados da loja** | Dados da loja: revise nome, cores, logo e textos e clique em **Salvar** | Obrigatório |
| **Domínio próprio** | Dados da loja → Domínio próprio (só nos planos com domínio próprio) | Opcional |
| **Pagamento** | Pagamentos: conecte o Mercado Pago ou a Cielo | Obrigatório |
| **Nota fiscal** | Dados da loja → Nota fiscal: escolha como a loja emite (vale "não emite pela plataforma") | Opcional |
| **Frete** | Dados da loja → Entrega e retirada: CEP de origem ou retirada no balcão | Obrigatório |
| **Mercado Livre, Instagram, OLX** | Só aparecem se o plano inclui o canal. O Mercado Livre se conecta em **Canais de venda** (4.6.1) e o passo se conclui com a conta conectada; Instagram e OLX chegam numa próxima versão | Opcional |

- O passo fica **Concluído** sozinho quando a configuração é salva na tela dele; dá para fazer aos poucos e voltar depois.
- **Configurar** abre a tela do passo. **Pular** esconde um passo opcional da lista de pendências; **Mostrar de novo** desfaz.
- **Publicar a loja** só fica disponível com **Dados da loja, Pagamento e Frete** concluídos. Ao publicar, a loja virtual abre na hora.
- Na loja personalizada, **Dados da loja** já conta como concluído (o visual é da equipe da plataforma).
- Em ambiente de teste, o pagamento simulado conta como configurado (a tela avisa).

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

**Preencher com IA (cadastro por foto)**

Em **Nova peça**, a IA pode preencher o cadastro a partir das fotos. Você sempre confere e salva; nada vai para a loja sozinho.

1. Em **Preencher com IA**, clique em **Escolher fotos** e selecione de 1 a 10 fotos da peça (JPG, PNG ou WebP, até 10 MB cada). Fotos com a **etiqueta e o código** à mostra ajudam muito.
2. Se quiser, escreva **o que você já sabe** (até 500 caracteres): código, veículo de onde saiu, lado. Ex.: "retirada de um Gol 2011 1.6; lado do motorista".
3. Clique em **Preencher com IA**. Em alguns segundos, a IA preenche **título**, **descrição**, **estado** e **códigos OEM**.
4. Os campos preenchidos pela IA ficam **destacados em roxo** até você conferir: mexa no campo ou clique em **Revisado**. Códigos OEM só aparecem se a IA conseguiu lê-los nas fotos — confira com a peça.
5. **Veículos sugeridos** aparecem marcados. **Desmarque** os que não servem. Veículos que a IA citou mas que não estão na tabela de veículos aparecem só como texto; se servirem, adicione à mão depois, em **Compatibilidade**.
6. Informe o **código interno**, o **preço** (a IA nunca sugere preço) e a quantidade. Confira o resto e clique em **Cadastrar**.

Ao cadastrar, a peça nasce como **rascunho**, as fotos escolhidas viram as **fotos da peça** e os veículos mantidos entram na **Compatibilidade** com o selo **IA**. Se alguma foto ou veículo não puder ser gravado, a peça é cadastrada assim mesmo e um aviso mostra o que faltou.

- **Limite do plano:** cada sugestão concluída conta 1 nos **cadastros com IA do mês** (veja 4.2). Foto recusada ou falha da IA não contam. No limite, a tela mostra a mensagem do plano.
- **Privacidade:** as fotos vão para a IA reduzidas e **sem a localização** gravada pelo celular.
- Se a IA não conseguir sugerir (foto escura, peça não identificável), a tela pede outras fotos ou o preenchimento à mão.

**Fotos**
- Na peça já cadastrada, **Adicionar fotos**: JPG, PNG ou WebP, até 10 MB cada, até 20 fotos.
- A **primeira foto é a capa**. Use ← e → para reordenar e **Remover** para tirar uma foto.
- Uma peça ativa não pode ficar sem foto.

**Compatibilidade (veículos em que a peça serve)**
- Escolha **Marca → Modelo → Versão** e, se quiser, o intervalo de anos (**De/Até**). Depois, clique em **Adicionar**.
- Com os anos em branco, vale a versão inteira.
- Peça sem veículo informado **não aparece na busca por veículo** da loja.
- O selo **IA** marca os veículos que vieram do cadastro por foto e que você manteve.

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

**Anúncios** (seção no cadastro da peça)
- Mostra onde a peça está anunciada fora da loja: canal, situação, quantidade e preço enviados e quando foi a última atualização.
- O **estoque manda**:
  - toda venda, reserva ou ajuste de quantidade atualiza o anúncio sozinho;
  - quando a peça **zera**, o anúncio fica **Pausado (sem estoque)**;
  - quando volta a ter saldo, ele **reativa sozinho**;
  - peça **inativa** também pausa o anúncio.
- **Pausar:** o anúncio fica **Pausado por você** e não volta sozinho, mesmo com estoque. **Retomar** faz ele voltar a seguir o estoque.
- **Conferência de hora em hora:** o sistema compara cada anúncio com o estoque da loja (situação, quantidade e preço) e corrige o canal se alguém mexeu lá. Vale o estoque da loja. Cada correção aparece em **Integrações** como "reconciliar anúncio", com o que mudou. A coluna **Conferido** mostra quando foi a última conferência.
- **Anúncio excluído ou encerrado direto no canal:** fica com erro, para a loja decidir.
- **Erro na última atualização:** o canal não respondeu, mesmo depois das novas tentativas automáticas. A próxima mudança de estoque tenta de novo, e o detalhe fica em **Integrações**.

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

**Dados fiscais da peça** (seção **Dados fiscais** no cadastro da peça)
- **NCM**, **CSOSN/CST** e **origem** da peça.
- Em branco, a peça usa o padrão da loja (**Dados da loja → Nota fiscal**).
- O CSOSN vale para o Simples Nacional e o CST para o Regime Normal, conforme o regime da loja.

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
| Devolvido | Todas as peças do pedido voltaram (devolução parcial não muda a situação; aparece no detalhe). |
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
| Entregue ou retirado | **Registrar devolução** | Veja "Devoluções" abaixo. |
| Venda de balcão (retirado, sem devolução) | **Cancelar pedido** | Para corrigir uma venda lançada errado. Motivo obrigatório; as peças voltam ao estoque. Devolva o dinheiro ao cliente, se for o caso. |

> **Estorno:** o cancelamento **não devolve o dinheiro sozinho**. Faça o estorno no site do gateway (Mercado Pago ou Cielo).
>
> **Pagamento depois do cancelamento:** se o comprador pagar um pedido que a loja cancelou, o pedido vai para **Em análise**, para a loja decidir entre aprovar e cancelar (estornando).

**Devoluções**
1. Abra o pedido **entregue** ou **retirado**, inclusive vendas de balcão, e clique em **Registrar devolução**. A tela mostra há quantos dias foi a entrega ou retirada (lembrete: 7 dias de arrependimento para compra fora da loja).
2. Informe **quantas unidades de cada peça** voltaram. Pode ser só parte do pedido e pode haver várias devoluções, nunca acima do que foi vendido.
3. Para cada peça, escolha se **volta ao estoque**. Peça **avariada** não volta: fica só registrada.
4. Escolha o **motivo**: defeito/garantia, peça errada, arrependimento ou outro.
5. Se a loja devolveu dinheiro, informe o **valor do reembolso** e a forma. O valor das peças aparece como sugestão. **Isso só registra:** o estorno é feito no gateway (Mercado Pago ou Cielo) ou no caixa.
6. Clique em **Registrar devolução**.

As peças que voltam ao estoque ficam disponíveis de novo na hora. Quando **todas** as peças voltam, o pedido fica **Devolvido** e o comprador vê isso na página do pedido. O detalhe mostra a lista de devoluções e o histórico registra cada uma.

> Depois de uma devolução, o pedido **não pode mais ser cancelado**, nem mesmo a venda de balcão. Use novas devoluções.

Se duas pessoas tentarem a mesma ação ao mesmo tempo, só a primeira vale. A outra recebe uma mensagem e vê o pedido atualizado.

**Nota fiscal (NF-e)**

A seção **Nota fiscal** aparece no detalhe do pedido depois do pagamento. O que ela mostra depende de como a loja emite (**Dados da loja → Nota fiscal**).

*Pela plataforma:*
1. Clique em **Emitir NF-e**.
2. Confira o **destinatário**. Retirada e venda de balcão não têm endereço: preencha o endereço e o CPF ou CNPJ do cliente.
3. Clique em **Emitir NF-e**. A nota fica **Processando na SEFAZ**, e a tela se atualiza sozinha.
4. Quando sair o resultado:
   - **Autorizada:** aparecem número, série e chave, e os botões **Baixar DANFE** e **Baixar XML**.
   - **Rejeitada:** aparece o motivo da SEFAZ. Corrija o que ela apontar (por exemplo, o NCM da peça) e clique em **Emitir de novo**. A nota rejeitada continua listada como histórico.

Antes de emitir, a tela avisa o que falta: configuração da loja incompleta, peça sem NCM, pedido ainda não pago. Cada pedido tem no máximo uma nota autorizada ou em processamento.

*Cancelar a NF-e:* em **Cancelar NF-e**, escreva a justificativa (15 a 255 caracteres) e confirme. O prazo é de **24 horas** depois da autorização, e a tela mostra até quando. Depois disso, para desfazer a venda é preciso uma **NF-e de devolução**, que ainda não está disponível. Cancelar a nota não cancela o pedido; cancelar o pedido não cancela a nota.

*Fora da plataforma:*
1. Emita a nota no seu emissor de sempre.
2. No pedido, informe a **chave de acesso** (44 números) ou anexe o **XML** da nota.
3. O sistema confere a chave e se a nota é do CNPJ da loja.
4. **Remover registro** tira a nota do pedido. Ela continua valendo: o cancelamento é feito no seu emissor.

> Em **homologação**, a nota é de teste e não tem valor fiscal. Hoje a emissão "pela plataforma" funciona só em modo de teste (simulado). A conexão real com a Focus NFe entra na próxima etapa.

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

### 4.5.1 Integrações

**Acesso:** menu **Integrações** (Dono e Operador). O detalhe de cada **pedido** e de cada **peça** também mostra as chamadas ligadas a ele, na seção "Integrações".

A loja conversa com serviços externos: gateway de pagamento (Mercado Pago ou Cielo), frete e, depois, canais de venda. Esta tela mostra cada uma dessas conversas e o que deu errado.

**Falhas pendentes**
- Quando um serviço fica fora do ar, o sistema **tenta de novo sozinho**: depois de 10 s, 30 s, 2 min, 10 min e 30 min.
- Se mesmo assim não der certo, aparece uma **falha pendente**, com o que estava sendo feito (por exemplo, "Confirmar pagamento no gateway"), o motivo e o número de tentativas.
- Enquanto houver falha pendente, o topo do painel mostra uma faixa amarela: **"N integrações falharam e precisam de atenção"**.
- Depois que o serviço voltar, clique em **Reprocessar**. Em alguns segundos o processamento roda de novo; se falhar outra vez, aparece uma nova falha.

**Log de chamadas**
- Toda chamada fica registrada: quando, serviço, operação (criar cobrança, consultar pagamento, cotar frete…), se deu certo, o pedido, o motivo do erro e a duração.
- Filtre por serviço, situação (deram certo / falharam) e período. O log guarda **90 dias**.
- Senhas, tokens, CPF e e-mails **nunca** aparecem no log.

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

**Loja personalizada** (site feito sob medida, contratado à parte)
- A página mostra o selo **Loja personalizada**.
- O visual foi feito pela equipe da plataforma: **logo, nome, cores e textos** (sobre, trocas e devoluções, rodapé) aparecem só para consulta. Para mudar, fale com o suporte. O tema e as páginas exclusivas (como "Quem somos") também são cuidados pela equipe.
- Continuam com a loja: entrega e retirada, ocultar peças sem estoque, rastreabilidade, nota fiscal, e-mail de envio e todo o resto do painel (peças, preços, estoque, pedidos, pagamentos).

**Nota fiscal (NF-e)**

Defina com o contador da loja. O CNPJ emitente é o da loja.
- **Como a loja emite:**
  - **Não emite pela plataforma** (padrão).
  - **Pela plataforma:** usa o provedor fiscal da loja (Focus NFe; a conexão real entra na próxima etapa).
  - **Fora da plataforma:** a loja emite no próprio emissor e informa a chave ou o XML no pedido.
- **Ambiente:** homologação (teste) ou produção.
- **Regime tributário:** Simples Nacional (CSOSN) ou Regime Normal (CST).
- **UF da loja**, que define o CFOP: dentro ou fora do estado do destinatário.
- **Padrões das peças:** NCM, CFOP dentro do estado (ex.: 5102), CFOP fora do estado (ex.: 6102), CSOSN/CST e origem.

Para emitir pela plataforma, UF, os dois CFOPs, CSOSN/CST e origem são obrigatórios.

**E-mail de envio** (fim da página, só o Dono)

A loja manda e-mails automáticos ao comprador:
- o link de acesso a **Meus pedidos**;
- os avisos de **pedido recebido**, **pagamento confirmado**, **pedido enviado** (com o rastreio) e **pedido cancelado**.

Venda de balcão não gera e-mail.
- **E-mail de contato da loja**: as respostas dos compradores vão para ele.
- Sem outra configuração, os e-mails saem do **remetente da plataforma** (mostrado na tela), com o nome da loja. Não tem custo extra.
- Para enviar pelo **e-mail da própria loja** (Google Workspace, Microsoft 365 ou outro), marque a opção e preencha:
  - servidor, porta e segurança;
  - usuário e senha;
  - nome e e-mail do remetente.

  No Google Workspace: servidor smtp.gmail.com, porta 587, STARTTLS e uma **senha de app** (não a senha normal da conta). A senha fica guardada cifrada e não aparece de novo. Para mantê-la, deixe o campo em branco ao salvar.
- **Enviar e-mail de teste**: envia na hora e mostra o erro do servidor, se houver.
- Se o servidor de e-mail falhar, o sistema tenta de novo sozinho. As tentativas aparecem em **Integrações**.

**Domínio próprio** (fim da página; planos Profissional e Completo)

A loja sempre funciona no endereço da plataforma (mostrado na tela). Com domínio próprio, ela abre também em, por exemplo, `www.sualoja.com.br`.
1. Digite o domínio (de preferência com "www") e clique em **Adicionar domínio**. Para o endereço sem "www" também funcionar, adicione os dois (um de cada).
2. A tela mostra os registros a criar no painel de DNS do domínio (Registro.br, GoDaddy, Cloudflare…):
   - **CNAME** do "www" para o endereço da loja na plataforma. No domínio sem "www", **ALIAS/ANAME** ou, se o provedor não tiver, **A** para o IP indicado.
   - **TXT** com o nome `_ecomm-verificacao…` e o código mostrado: prova que o domínio é da loja.

   Os registros de e-mail do domínio não são tocados.
3. O sistema confere sozinho a cada poucos minutos, por até 72 horas. A mudança no DNS pode levar algumas horas para valer. Situações:
   - **Aguardando o DNS**: ainda não encontrou os registros; a tela diz o que falta.
   - **Funcionando**: o domínio vira o **endereço principal**. Quem entra pelo endereço antigo é levado para ele. O cadeado (HTTPS) é emitido no primeiro acesso.
   - **Não encontramos os registros**: passaram 72 horas. Corrija o DNS e clique em **Verificar agora** (vale também para conferir na hora).
- **Remover** tira o domínio na hora; a loja volta a abrir no endereço da plataforma.
- Um domínio só pode estar em uma loja.
- Nos outros planos, a seção mostra como mudar de plano.

**Privacidade e termos** (LGPD)

A loja virtual sempre tem as páginas **Política de privacidade** e **Termos de uso**, com links no rodapé e no momento de confirmar o pedido.
- Em branco, a loja mostra um **modelo padrão** já com a razão social e o CNPJ (aparece como sugestão no campo).
- **Começar pelo modelo** copia o texto padrão para você ajustar; **Voltar ao modelo padrão** apaga o seu texto.
- Recomendamos revisar os textos com um advogado.
- Vale também para a loja personalizada: são textos jurídicos da loja, não aparência.

### 4.6.1 Canais de venda

**Acesso:** menu **Canais de venda** (só o Dono).

Aqui a loja liga as contas onde vende. Nesta versão dá para **conectar a conta do Mercado Livre**; publicar anúncios, receber as vendas e as perguntas de lá chegam nas próximas versões. Instagram e OLX também.

**Conectar o Mercado Livre**
1. Clique em **Conectar Mercado Livre**. O painel abre o site do Mercado Livre.
2. Entre com a **conta principal** da loja no Mercado Livre (a de administrador). Conta de colaborador não consegue autorizar.
3. Confira o pedido de acesso e clique em **Autorizar**. O Mercado Livre devolve você ao painel, que conclui a conexão sozinho.
4. A conta aparece como **Conectada**, com o apelido da loja no Mercado Livre.

- Cada loja liga **uma** conta do Mercado Livre. Para trocar de conta, desconecte a atual antes.
- Uma conta do Mercado Livre só pode estar ligada a **uma loja** da plataforma.
- O link de autorização vale **10 minutos** e uma vez só. Se demorar ou recarregar a página de volta, clique em **Conectar** de novo.
- Negou o acesso no Mercado Livre? Nada é conectado; é só tentar de novo quando quiser.
- O plano da loja precisa incluir o Mercado Livre.

**Acesso renovado sozinho**
O Mercado Livre libera o acesso por algumas horas de cada vez. O sistema renova sozinho, antes de vencer, sem você fazer nada. As renovações aparecem em **Integrações** (serviço Mercado Livre).

**Autorizar de novo**
Se o Mercado Livre recusar a renovação (por exemplo, a senha da conta foi trocada ou o acesso foi retirado lá no Mercado Livre), a conexão fica como **Autorizar de novo**:
- uma faixa aparece no topo do painel para todos os perfis;
- os anúncios dessa conta **param de ser atualizados** pelo estoque da loja até a nova autorização;
- clique em **Autorizar de novo** e repita os passos acima com a mesma conta.

**Desconectar**
Clique em **Desconectar** e confirme. O sistema retira a autorização da plataforma na sua conta do Mercado Livre e apaga o acesso guardado. Os anúncios que já existem no Mercado Livre continuam lá, mas deixam de acompanhar o estoque da loja. Conectar a mesma conta de novo retoma tudo.

> O acesso ao Mercado Livre fica guardado no **Cofre** da loja, cifrado, e nunca é mostrado.

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

### 4.7.1 Cofre e certificado digital

**Acesso:** menu **Cofre** (só Dono).

Chaves e certificados ficam guardados **cifrados**. Depois de salvos, nunca aparecem de novo.

**Certificado digital A1** (usado na NF-e)
1. Em **Certificado digital A1**, escolha o arquivo **.pfx** (ou .p12) que a certificadora entregou e informe a **senha** dele.
2. Clique em **Enviar certificado**. Antes de guardar, o sistema confere:
   - se a senha abre o arquivo;
   - se o arquivo tem a chave privada (certificado A1 completo);
   - se ainda está na validade;
   - se é do **CNPJ da loja**.
3. A tela mostra o titular, o CNPJ e a validade. Para renovar, envie o novo com **Trocar certificado**. **Remover certificado** apaga o arquivo e a senha.

**Aviso de vencimento**
- A partir de **30 dias** antes do vencimento, o Dono vê uma faixa no topo do painel, em qualquer página, com a data e o atalho para o Cofre.
- O aviso fica mais forte aos **15** e aos **5 dias** e depois de vencido.
- A equipe da plataforma também é avisada nesses dias.

**O que está guardado:** lista de tudo o que a loja guardou no cofre, com o tipo, o item, os últimos 4 caracteres (quando é uma chave de texto), a validade e a data da última atualização. As chaves dos gateways e a senha do e-mail continuam sendo cadastradas em **Pagamentos** e em **Dados da loja**.

> Com a Focus NFe (na próxima etapa), o certificado pode também precisar ser enviado à própria Focus. A tela vai orientar quando a conexão estiver disponível.

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

### 4.9 Privacidade (LGPD) e exportação

**Acesso:** menu **Privacidade** (só Dono).

**Apagar dados de um comprador** — quando um comprador pedir (a lei dá até 15 dias para atender):
1. Digite o **e-mail** ou o **CPF/CNPJ** que ele usou nos pedidos e clique em **Procurar pedidos**.
2. A tela mostra quantos pedidos foram encontrados e quantos têm nota fiscal.
3. Se houver pedido **em andamento** (aguardando pagamento, pago, em análise ou enviado), a tela mostra os números: conclua ou cancele antes.
4. Clique em **Apagar dados deste comprador** e confirme. **Não dá para desfazer.**

O que acontece:
- Saem e-mail, telefone e endereço de entrega (ficam só cidade e UF) e o acesso dele a **Meus pedidos**.
- Pedidos **sem nota fiscal**: o nome vira "Titular anonimizado" e o CPF/CNPJ sai.
- Pedidos **com nota fiscal**: nome e CPF/CNPJ ficam, porque a lei fiscal obriga a guardar por 5 anos. A nota em si não muda.
- Pedido, peças, valores e histórico continuam.

**Exportar dados da loja**
1. Clique em **Gerar exportação**. O pacote é montado em segundo plano; a lista atualiza sozinha.
2. Quando a situação for **Pronta**, clique em **Baixar**. O arquivo (ZIP) pode ser baixado por **7 dias**; depois, gere outro.
- O pacote tem planilhas (abrem no Excel) de peças, códigos OEM, compatibilidade, veículos doadores, movimentos de estoque, pedidos e itens, histórico, devoluções, clientes, notas fiscais, anúncios e dados da loja, além das fotos originais e dos arquivos XML/DANFE das notas.
- Senhas, chaves de pagamento e o certificado digital **não** vão no pacote.
- O pacote tem dados pessoais de compradores: guarde em local seguro.
- Uma exportação por vez.

**Quem acessou dados pessoais** — lista, que não pode ser alterada nem apagada, de cada vez que alguém da loja abriu um pedido, baixou o XML ou o DANFE de uma nota, gerou ou baixou uma exportação ou apagou dados de um comprador: quando, quem, o pedido e o IP. Filtre por ação.

### 4.10 Assinatura

**Acesso:** menu **Assinatura** (só Dono).

Mostra o plano da loja, o valor da mensalidade e as faturas da plataforma (mensalidades e taxa de implantação), com vencimento, valor e situação: **Em aberto**, **Vencida**, **Paga** ou **Cancelada**.

- **Quando começa:** a mensalidade começa no dia em que a loja é publicada (**Primeiros passos**, 4.2.1). A primeira vence nesse dia; as seguintes, todo mês no mesmo dia (quem publica nos dias 29, 30 ou 31 passa a vencer no dia 28).
- **Fatura do mês:** aparece cerca de 10 dias antes do vencimento, com o valor do plano naquele momento.
- **Taxa de implantação:** quando houver, é lançada pela equipe da plataforma e aparece aqui.
- **Pagar:** clique em **Pagar** na fatura. Você vai para a página de pagamento e, ao terminar, volta para esta tela. A fatura aparece como **Paga** assim que o pagamento é confirmado (alguns instantes). Clicar em **Pagar** de novo abre a mesma cobrança, nunca uma segunda.
- **Pagou por Pix ou transferência direto para a plataforma?** A equipe registra o pagamento e a fatura aparece como **Paga (registrada pela equipe)**.
- Se a tela avisar que o pagamento pelo painel não está disponível, fale com a equipe da plataforma.

**Fatura em atraso:** contando do vencimento da fatura em aberto mais antiga:

| Atraso | O que acontece |
|---|---|
| 3 dias | Uma faixa no topo de todas as telas do painel avisa a fatura vencida. |
| 10 dias | **Painel somente leitura**: dá para ver tudo, mas não alterar (peças, pedidos, dados da loja…). A loja virtual e os canais **continuam vendendo** e os pedidos continuam chegando. |
| 20 dias | **Loja suspensa**: a loja virtual sai do ar e os anúncios no Mercado Livre, Instagram e OLX ficam pausados. |
| 60 dias | A loja fica **em encerramento** (a equipe da plataforma entra em contato; nada é apagado sozinho). |

- Mesmo com o painel somente leitura, continuam valendo: **Assinatura** (ver e pagar faturas), **Privacidade** (exportação e pedidos dos compradores sobre dados pessoais), trocar a senha e sair.
- **Pagou, voltou:** assim que o pagamento é confirmado (ou registrado pela equipe) e não sobra fatura vencida, a loja volta ao normal na hora — painel liberado, loja virtual no ar e anúncios retomados.
- Todos os perfis veem a faixa; só o Dono vê as faturas e paga.

## 5. Loja virtual (o que o comprador vê)

**Loja em breve:** enquanto o Dono não publica a loja (4.2.1), o endereço mostra só "Loja em breve". Loja suspensa mostra "Loja indisponível no momento".

**Navegar e buscar**
- **Início**: nome, texto "Sobre a loja" e peças recentes.
- **Busca**: por texto e por veículo (marca, modelo e ano).
- **Página da peça**: fotos, preço, estado, código, quantidade disponível, descrição e veículos em que serve ("Serve em").

**Comprar**
1. Na peça, escolha a quantidade e clique em **Adicionar ao carrinho**.
2. No **Carrinho**, informe o **CEP** e clique em **Calcular frete**. Depois, escolha uma opção de entrega ou a **retirada no balcão**, quando a loja oferece.
3. Clique em **Finalizar compra** e preencha nome, e-mail, telefone, CPF e, na entrega, o endereço.
4. Clique em **Confirmar pedido**. Se preço, estoque ou frete mudaram, a loja avisa e pede para rever.
5. O comprador vai para a **página do pedido** e recebe o e-mail **Pedido recebido**, que traz o link dessa página.

**Pagar:** na página do pedido, **Pagar agora** abre a página do gateway. As peças ficam reservadas até o prazo mostrado.

**Acompanhar o pedido**
- Etapas: **Pedido feito → Pago → Enviado → Entregue**, ou **Pedido feito → Pago → Retirado** na retirada.
- Com o pedido enviado, aparece o **código de rastreio**.
- Pedido cancelado mostra o motivo. Pedido em análise mostra que a loja vai entrar em contato.
- A cada mudança importante (pago, enviado, cancelado), o comprador recebe um e-mail.

**Meus pedidos** (link no topo da loja)

Não precisa de senha.
1. O comprador informa o e-mail usado nas compras.
2. Se houver pedidos com esse e-mail **nesta loja**, ele recebe um link. O link vale **15 minutos** e serve **uma vez só**. A tela é sempre a mesma, para não revelar quem comprou.
3. Ao abrir o link, ele vê a lista de pedidos (situação, data, total) e o detalhe de cada um.
4. O acesso fica guardado naquele navegador por **30 dias**. Para encerrar antes, use **Sair**.

Pedido aguardando pagamento: por **Meus pedidos** não há botão de pagar. O comprador paga pelo botão do e-mail **Pedido recebido**.

Limites para evitar abuso:
- até 5 links por e-mail por hora;
- até 10 pedidos de link por minuto do mesmo computador.

**Trocas e devoluções:** página com o texto da loja.

**Política de privacidade** e **Termos de uso:** páginas com os textos da loja (ou o modelo padrão), com links no rodapé. Ao confirmar o pedido, o comprador vê o aviso de que concorda com os termos e a política.

**Loja personalizada:** o comprador vê o visual sob medida (cabeçalho, rodapé e cores próprias) e as páginas exclusivas, com links no rodapé. Carrinho, checkout e **Meus pedidos** mantêm o layout padrão da plataforma.

## 6. Painel da plataforma (superadmin)

*Usado pela equipe da plataforma, não pela loja.* É um painel separado: a conta de superadmin não é a mesma conta de nenhuma loja, e quem entra no painel de uma loja não vê nada daqui (nem o contrário).

### 6.1 Primeiro acesso

1. A equipe técnica cria a conta pelo comando `criar-superadmin --email <e-mail>` do migrador (ver `CLAUDE.md`, seção Comandos) e repassa a senha provisória por um canal seguro.
2. Em **/plataforma**, entre com o e-mail e a senha provisória.
3. Crie uma senha só sua (de 10 a 128 caracteres).
4. Cadastre um **app autenticador** no celular (Google Authenticator, Microsoft Authenticator, Authy ou outro): clique em **Gerar chave**, no app escolha *adicionar conta* → *inserir chave de configuração* e digite a chave mostrada (ou, no celular, use **abrir direto no app**). Digite o código de 6 dígitos que o app mostrar e clique em **Ativar**.
5. Aparecem **10 códigos de recuperação**. Guarde-os em lugar seguro: cada um serve uma única vez, no lugar do código do app, se você perder o celular. Eles não aparecem de novo.

### 6.2 Entrar no dia a dia

E-mail e senha, depois o código de 6 dígitos do app (ou um código de recuperação). A sessão dura até 8 horas; depois, entre de novo. **Sair** encerra a sessão na hora.

Cinco erros seguidos (senha ou código) bloqueiam a conta por 15 minutos. Um mesmo código do app não vale duas vezes.

### 6.3 Lojas

- **Lista**: busca por nome, endereço, domínio ou CNPJ completo, e filtro por situação (ativa, suspensa…).
- **Nova loja**: nome fantasia, razão social, CNPJ, endereço (o começo do endereço da loja: letras minúsculas, números e hífen) e e-mail do Dono. Ao criar, aparece a **senha provisória do Dono** — só nesse momento. Repasse ao Dono por um canal seguro; no primeiro acesso ele cria a própria senha. Se o e-mail já tinha conta na plataforma, o Dono entra com a senha que já usa. A loja nasce **Em implantação**: a loja virtual mostra "Loja em breve" até o Dono publicar pelos **Primeiros passos** (4.2.1).
- **Abrir uma loja** mostra dados cadastrais, endereços (subdomínio e domínio próprio), usuários e perfis. Peças, pedidos e clientes da loja **não** aparecem aqui.
  - **Plano**: escolha e clique em **Trocar plano**. Vale na hora; ao baixar de plano, o que já passou do novo limite continua, só não dá para acrescentar mais.
  - **Suspender loja** (pede confirmação): a loja virtual sai do ar, os anúncios nos canais são pausados e o painel da loja fica somente para consulta. **Reativar loja** desfaz (os anúncios voltam). Loja em implantação não pode ser suspensa: a loja virtual dela já está fechada, e reativar a publicaria sem os passos obrigatórios. A suspensão pela equipe não sai sozinha com pagamento.
  - **Bloqueada pela cobrança**: quando a situação (somente leitura, suspensa ou em encerramento) veio do atraso de fatura (veja 4.10), a tela avisa e **Reativar** não vale: registre o pagamento ou cancele a fatura em **Cobrança** e a loja volta sozinha.
  - **Loja personalizada** (RF40): **Ligar** depois de fechado o orçamento; o Dono deixa de mudar logo, nome, cores e textos. **Desligar** devolve a edição ao Dono e a loja volta ao visual padrão na hora.
  - **Tema e páginas exclusivos**: abre o editor do visual sob medida (veja 6.3.1).
  - **Cobrança**: plano, mensalidade, desde quando a loja é cobrada e o dia do vencimento, e as faturas da loja.
    - **Taxa de implantação**: informe o valor e o vencimento e clique em **Lançar** (uma por loja; para trocar, cancele a atual).
    - **Registrar pagamento**: para o que a loja pagou fora do gateway (Pix, transferência). Informe como foi pago.
    - **Cancelar**: informe o motivo (por exemplo, cortesia). Fatura paga ou cancelada não muda mais.
    - As mensalidades são geradas sozinhas, cerca de 10 dias antes do vencimento, a partir da publicação da loja. Mensalidade de valor zero não gera fatura, e o sistema não cobra meses passados que ficaram sem fatura.

#### 6.3.1 Tema e páginas exclusivos

Onde a equipe monta o site sob medida da loja personalizada. Pode ser preparado antes de ligar a opção: a loja virtual só usa o tema e as páginas enquanto a **Loja personalizada** estiver ligada.

- **Tema**:
  - **CSS**: o visual (cores, fontes, espaçamentos, imagens de fundo). Vale no cabeçalho, no rodapé e nas páginas da loja; **carrinho, checkout e Meus pedidos** continuam com o layout padrão (só com as cores da loja), para não atrapalhar a venda.
  - **Cabeçalho**: aparece acima do menu da loja (faixa de promoção, banner).
  - **Rodapé**: substitui o texto do rodapé. Os links de termos, privacidade e trocas continuam.
- **Pré-visualizar** mostra como fica, num quadro isolado, sem salvar. **Salvar tema** grava; a loja mostra a mudança em até 1 minuto.
- **Páginas** (até 20): título, endereço (a página abre em `/p/endereço`, por exemplo `/p/quem-somos`), ordem no rodapé, conteúdo e **Publicada**. Rascunho não aparece na loja. **Remover página** pede confirmação.
- **O que é aceito**: títulos, textos, imagens, links, listas e tabelas. Links e imagens só com `https`, `mailto:`, `tel:` ou endereços da própria loja. O visual vem do CSS; não há estilo dentro do HTML.
- **O que é removido ao salvar**: script, eventos (como `onclick`), iframes, formulários, links `javascript:` ou sem `https`, `@import` e qualquer regra de CSS fora da lista permitida. A tela mostra **o que a limpeza removeu**.
- Cada gravação fica na auditoria, com o que foi removido.

### 6.4 Planos

Mostra os limites (usuários, peças ativas, cadastros por IA no mês) e os recursos de cada plano, e quantas lojas usam cada um. Mudar limites e recursos exige atualização do sistema.

**Mensalidade**: digite o valor em reais e clique em **Salvar** na linha do plano. Vale para as faturas geradas depois (as já geradas não mudam). Zero = o plano não é cobrado.

### 6.5 Auditoria

Tudo o que os superadmins fazem fica registrado e não pode ser alterado nem apagado: entradas (e tentativas recusadas), cadastro do app, uso de código de recuperação, troca de senha, **cada abertura de uma loja** e cada ação (criar, plano, suspender, reativar, loja personalizada, tema e páginas exclusivos, mensalidade dos planos, taxa de implantação, pagamento registrado e fatura cancelada). Filtre por ação ou e-mail; na tela de uma loja, **Ver o que a plataforma fez nesta loja** mostra só os registros dela.

### 6.6 Comandos do servidor

Continuam disponíveis no migrador, para a equipe técnica: `criar-superadmin`, `criar-loja`, `plano`, `loja-personalizada` e `importar-veiculos` (tabela de veículos usada na compatibilidade).
## 7. Ainda não disponível

Itens previstos que **ainda não estão no sistema**:

- Mercado Livre: publicar anúncios (com categoria, atributos e compatibilidades), receber vendas, perguntas e avisos. A conexão da conta já funciona (4.6.1); em teste, o Mercado Livre é simulado (página "Mercado Livre simulado", sem conta real).
- Instagram (catálogo Meta) e OLX: conectar a conta e publicar. A seção **Anúncios** e a atualização automática pelo estoque já estão prontas para recebê-los.
- Emissão real de NF-e pela Focus NFe: hoje "pela plataforma" é simulado. "Fora da plataforma" já funciona.
- NFC-e (cupom fiscal eletrônico do balcão) e emissão automática da NF-e ao confirmar o pagamento.
- Frete real (Melhor Envio): hoje o frete é simulado.
- Compatibilidade com veículos e fotos pela planilha de importação; atualização em massa de peças já cadastradas.
- Importação dos anúncios existentes do Mercado Livre.
- Envio automático da rastreabilidade ao banco de dados do Detran; rastreabilidade pela planilha de importação.
- Alerta de falha de integração por e-mail (hoje o alerta é a faixa no painel).
- Remetente da plataforma em produção (provedor ainda a escolher). Em teste, os e-mails ficam numa caixa interna e não chegam ao comprador.
- NF-e de devolução e estorno automático do reembolso.
- Automações de IA: atendimento, preço sugerido, alertas e resumo diário. No cadastro por foto, a categoria e os atributos do Mercado Livre virão com a integração do Mercado Livre. Em teste, a IA é simulada e sempre sugere o mesmo farol de exemplo.
- Configuração do prazo de pagamento pelo painel (hoje, 30 minutos).
- Assinatura: pagamento real pelo painel (o gateway da plataforma ainda será escolhido; em teste, a página de pagamento é simulada) e aviso da fatura por e-mail (hoje o aviso é a faixa no painel).
- Primeiros passos: ver a loja virtual antes de publicar e "despublicar" a loja pelo painel.
- LGPD: o comprador pedir a exclusão sozinho pela loja virtual (hoje pede à loja, que apaga em **Privacidade**); observações digitadas à mão nos pedidos não são apagadas na anonimização.
- Painel da plataforma: criar outros superadmins pela tela (hoje, só a equipe técnica, pelo servidor); trocar o app autenticador ou gerar novos códigos de recuperação (hoje não há como — sem o celular e sem códigos, a equipe técnica cria outra conta); QR code no cadastro do app; ver peças e pedidos de uma loja; enviar imagens do tema exclusivo pela tela (hoje, só por endereço `https`); importar a tabela de veículos pela tela.

## 8. Histórico do manual

| Data | Mudança |
|---|---|
| 11/10/2026 | Primeira versão: acesso, perfis, peças, pedidos (RF17), dados da loja, pagamentos, usuários e loja virtual. |
| 11/10/2026 | Venda de balcão (RF18): nova seção 4.5; origem Balcão, busca por CNPJ e cancelamento da venda de balcão em Pedidos. |
| 11/10/2026 | Importação de peças por planilha (RF11), na seção 4.3 Peças. |
| 11/10/2026 | Veículos doadores, rastreabilidade e modo desmontadora (RF10): seção 4.3.1 e perfis. |
| 12/10/2026 | Devoluções (RF20): situação Devolvido e passo a passo em 4.4 Pedidos. |
| 12/10/2026 | Integrações (RF28/RF29): seção 4.5.1, faixa de alerta de falhas e perfis. |
| 12/10/2026 | Área do cliente e e-mails ao comprador (RF16): **Meus pedidos** na loja virtual e **E-mail de envio** em 4.6 Dados da loja. |
| 12/10/2026 | NF-e (RF19 parte A): nota fiscal no pedido (4.4), dados fiscais da peça (4.3.1), configuração em 4.6 e perfis. |
| 12/10/2026 | Loja personalizada (RF40): selo e aparência travada em 4.6 Dados da loja; comando de operação em 6. |
| 13/10/2026 | Cofre e certificado digital A1 (RF06): nova seção 4.7.1, aviso de vencimento e perfis. |
| 13/10/2026 | Anúncios da peça (base do E2, RF24): situação nos canais, pausa e retomada, atualização automática pelo estoque (4.3). |
| 13/10/2026 | Conferência de hora em hora dos anúncios com os canais (RF27), na seção Anúncios (4.3). |
| 13/10/2026 | Planos e limites (RF37): cartão Seu plano e limites em 4.2; comando de troca de plano em 6. |
| 13/10/2026 | Domínio próprio (RF03): passo a passo em 4.6 Dados da loja. |
| 15/10/2026 | Painel da plataforma (RF07): seção 6 reescrita (primeiro acesso com app autenticador, lojas, planos, auditoria); endereço em 2 e visão geral. |
| 15/10/2026 | LGPD (RNF04/RF39): privacidade e termos em 4.6 e na loja virtual (5); nova seção 4.9 Privacidade (apagar dados de comprador, exportação, registro de acesso); perfis. |
| 16/10/2026 | Tema e páginas exclusivos da loja personalizada (RF40): nova seção 6.3.1; loja virtual (5); auditoria em 6.5. |
| 16/10/2026 | Primeiros passos (RF05): nova seção 4.2.1, cartão no Início, "Loja em breve" na loja virtual (5), loja nova em implantação e suspensão em 6.3; perfis. |
| 16/10/2026 | Assinatura (RF38): nova seção 4.10 (mensalidade, faturas e pagamento); Cobrança da loja em 6.3, mensalidade dos planos em 6.4, auditoria em 6.5; perfis. |
| 16/10/2026 | Base da IA (RF35/RF36): limite de cadastros com IA no mês explicado em 4.2 (Limites do plano); o cartão Seu plano passa a mostrar o uso real. |
| 16/10/2026 | Fatura em atraso (RF38): faixa, painel somente leitura, suspensão com anúncios pausados e volta ao pagar (4.10); suspensão e bloqueio pela cobrança em 6.3. |
| 16/10/2026 | Canais de venda (RF21, parte 1): nova seção 4.6.1 (conectar o Mercado Livre, renovação, autorizar de novo, desconectar), faixa de alerta e perfis; passo Mercado Livre dos primeiros passos leva à tela. |
| 16/10/2026 | Cadastro por foto (RF30): **Preencher com IA** em 4.3 Peças (sugestão, destaque até revisar, veículos sugeridos, limite do plano) e selo IA na compatibilidade. |
