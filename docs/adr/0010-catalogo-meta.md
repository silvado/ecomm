# ADR-0010 — Catálogo do Instagram/Facebook (Meta)

- **Status:** Aceito
- **Data:** 2026-10-18
- **Requisitos:** RF22 (CA1–CA3), RF24, RF06, RF37

## Contexto

O RF22 pede um feed de catálogo por loja, num endereço não adivinhável e no formato do Commerce Manager (CA1). Preço e disponibilidade têm de ser atualizados na hora, e zerar o estoque tem de chegar em segundos pela Catalog Batch API, porque o feed agendado é de no máximo uma vez por hora (CA2). O link do item leva à página da peça na loja (CA3).

Fontes, consultadas em 17/10/2026:
- [Catalog Batch API](https://developers.facebook.com/docs/marketing-api/catalog-batch/reference):
  - chamada: `POST /{catalog_id}/items_batch` (Graph API v23.0), com `item_type=PRODUCT_ITEM`, `requests[]` (`method` CREATE/UPDATE/DELETE e `data`) e `allow_upsert` (padrão `true`);
  - limites: até 5000 itens e 28 MB por chamada;
  - resposta: `handles` e `validation_status`;
  - erros: 80014 (muitas chamadas no catálogo), 100 (parâmetro), 190 (token inválido) e 200 (permissão).
- [Campos do catálogo](https://developers.facebook.com/docs/commerce-platform/catalog/fields):
  - obrigatórios: `id`, `title`, `description`, `availability` (`in stock`/`out of stock`), `condition` (`new`/`refurbished`/`used`), `price` ("9.99 BRL", com ponto), `link`, `image_link` e `brand`;
  - opcionais: `additional_image_link` (até 20) e `quantity_to_sell_on_facebook`;
  - imagem: JPEG ou PNG de pelo menos 500×500.

## Decisões

### 1. Ligar com id do catálogo e token de usuário do sistema

A loja informa o id do catálogo e um token de acesso de **usuário do sistema** do Gerenciador de Negócios, com permissão no catálogo, como já faz com as chaves dos gateways (RF15). Não usamos o login da Meta (OAuth) nesta etapa, porque exigiria um app da plataforma revisado pela Meta.

Antes de ligar, a plataforma confere id e token em `GET /{catalog_id}?fields=name`. O token fica no cofre (`ApiKey` `meta:token`), e a conexão fica em `channel_connections` (canal `meta`, conta = id do catálogo, sem vencimento de token), com o catálogo no mapa da plataforma. O canal precisa estar no plano (Profissional e Completo, conforme a Q41). Desconectar apaga os segredos e o endereço do feed deixa de funcionar.

### 2. Feed CSV num endereço com segredo (CA1)

O feed é `GET https://api.<domínio>/api/catalogo/{loja}/{segredo}.csv`, público e com limite de taxa. O segredo tem 24 bytes aleatórios, fica no cofre (`meta:feed`) e é comparado em tempo constante. Com segredo errado, a resposta é a mesma de endereço inexistente.

Entram as peças **ativas com foto**:
- **Disponibilidade:** a peça sem saldo vai como `out of stock`, porque o item continua no catálogo.
- **Preço:** "350.00 BRL".
- **Link:** `https://{loja}.{domínio}/peca/{id}-{título}` (CA3).
- **Imagens:** JPEG (veja a seção 4), e as demais fotos em `additional_image_link`.
- **Estoque:** `quantity_to_sell_on_facebook` = saldo.
- **Marca (HIPÓTESE):** a marca do primeiro veículo compatível; sem compatibilidade, o nome da loja. A peça não tem campo de marca.

### 3. Atualização na hora pela Catalog Batch API (CA2)

Cada `StockChanged` de loja com o catálogo ligado publica `SyncMetaCatalogItem`. Esse handler lê o saldo e o preço atuais e faz um `UPDATE` (com `allow_upsert`) no item, com registro no log de integração.

Não há anúncio por peça (`channel_listings`), porque o feed já cobre todas as peças. Peça inativa vai como `out of stock`. Token recusado (190/200) deixa a conexão em "autorizar de novo"; limite de chamadas (80014) ou Meta fora do ar fazem a fila tentar de novo (RF28).

O handler usa o banco da plataforma (plano, loja), por isso declara `[Transactional(typeof(TenantDbContext))]`.

**Correção que vale para todos os canais:** a edição da peça passou a publicar `StockChanged` também quando só o preço muda. Antes, um preço novo só chegava aos canais na conferência de hora em hora (RF27).

### 4. Imagem em JPEG

A loja virtual serve as fotos em WebP, que a Meta não aceita. A rota `/api/loja/fotos/{foto}/catalogo.jpg` converte o original (privado) em JPEG de até 1920 px, sem metadados (`PhotoProcessor.ForChannelAsync`), com cache de um ano. Vale para peça ativa, mesmo sem saldo.

### 5. Graph API simulada

`Meta:UseFake` liga `FakeMetaGraphApi`, um `HttpMessageHandler` em memória por baixo do cliente real (`MetaCatalogClient`). A API recusa subir em produção com ela.

## Consequências

- **Teste real:** exige catálogo e usuário do sistema no Gerenciador de Negócios da loja. O feed precisa estar num endereço público (VPS).
- **Pendente:**
  - **Vendas pelo checkout do Instagram/Facebook:** se a loja usar, chegam por outro fluxo, ainda fora do escopo.
  - **Login da Meta (OAuth):** só no lugar do token colado, depois da revisão do app.
  - **Excluir do catálogo a peça inativada:** hoje ela fica como fora de estoque.
