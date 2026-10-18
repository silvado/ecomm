# ADR-0014 — Importação dos anúncios existentes do Mercado Livre

- **Status:** Aceito
- **Data:** 2026-10-18
- **Requisitos:** RF11 (CA2, CA3), RF24, RF27, RNF02
- **Relacionados:** [ADR-0002](0002-fila-e-outbox.md) (fila), importação por planilha (RF11 CA1, Q33)

## Contexto

As lojas que já vendem no Mercado Livre não devem recadastrar as peças. O RF11 CA2 pede que a importação:
- liste os anúncios ativos e pausados do vendedor;
- crie peças vinculadas (`ChannelListing`);
- não duplique quando for repetida.

O CA3 pede que rode em segundo plano, com progresso visível.

## Decisões

### 1. Fontes da API (regra 8)

- **Lista do vendedor:** `GET /users/{id}/items/search?search_type=scan`, seguindo o `scroll_id`. É o modo indicado para contas com mais de 1.000 anúncios. Fonte: "Itens e buscas" (https://developers.mercadolivre.com.br/itens-e-buscas; versão em espanhol https://developers.mercadolibre.com.ar/es_ar/items-y-busquedas).
- **HIPÓTESE até testar com o app real (DevCenter):** os ids vêm em `results`, o cursor em `scroll_id`, e uma página vazia encerra. As páginas oficiais recusam leitura automática (403); a confirmação veio por busca.
- **Anúncio:** `GET /items/{id}`, já usado na reconciliação, e descrição em `GET /items/{id}/description` (`plain_text`). O multiget (`/items?ids=`) fica de fora até confirmar o limite e o formato.
- **Fotos:** baixadas dos endereços do próprio anúncio, **só** de `https://*.mlstatic.com` (porta padrão), com no máximo 10 MB. O endereço vem de fora e não pode levar o servidor a outros destinos (SSRF). As fotos passam pelo mesmo processamento do envio pelo painel (WebP em 3 tamanhos, sem metadados).

### 2. Processo

- `channel_imports` (com RLS) guarda os ids, o cursor, o progresso e o relatório (até 500 itens).
- **Início:** `StartChannelImport` (síncrono, com trava por loja). Uma importação por vez; sem conta ativa do ML, recusa.
- **Lista:** `ListChannelImportPage` lê uma página por mensagem e encadeia a próxima. Para no limite de 5.000 anúncios.
- **Importação:** `ProcessChannelImport` em lotes de 10, porque cada anúncio custa duas chamadas e as fotos. Cada lote é uma transação do Wolverine: peças, saldos, movimentos, vínculos, `StockChanged` e progresso, com a linha travada.
- **Falhas:**
  - ML fora do ar ou limite de taxa: a fila tenta de novo o mesmo passo;
  - nova autorização pedida: a importação termina como "falhou", com o motivo.

### 3. Regras (`ChannelImportRules`)

| Anúncio | Resultado |
|---|---|
| Já vinculado nesta conta | pulado ("já vinculado"): reimportar não duplica |
| Ativo | peça **ativa** + vínculo `Active` |
| Pausado | peça ativa + vínculo `PausedByUser` (o sistema não reativa) |
| Encerrado, em revisão, sem preço, com variações | não entra; o motivo vai para o relatório |
| Código igual ao de peça ativa da loja | ligado a ela; a peça não muda e vale o estoque da loja (confirmado pelo usuário em 18/10/2026) |
| Código igual ao de peça inativa ou já anunciada nesta conta | não entra, com o motivo |
| Sem foto acessível | peça em **rascunho**, sem vínculo |

- **Código:** SELLER_SKU, depois `seller_custom_field`, depois o id do anúncio.
- **Condição:** `condition` do anúncio; ITEM_CONDITION "Recondicionado" vira recondicionada.
- **OEM:** PART_NUMBER ou OEM.
- **Opções do anúncio:** categoria, tipo, atributos e garantia ficam guardados no vínculo, como na publicação pela plataforma.
- **Por que a peça sem foto fica sem vínculo:** peça sem foto não pode ficar ativa, e anúncio de peça inativa recebe quantidade 0 na sincronização. Isso pausaria um anúncio que estava vendendo.
- **Estoque da loja manda (RF27):** o vínculo guarda a quantidade e o preço que estão no ML. Se a loja tiver outro saldo, o `StockChanged` leva o saldo da loja ao anúncio. A baixa continua atômica (RNF02): a importação só cria saldo novo e nunca altera o de peça existente.

## Compatibilidades dos anúncios (18/10/2026)

Na peça nova, os veículos do anúncio viram compatibilidades com origem `MlImport` (selo "ML" no painel).

- **Leitura:** `GET /items/{id}/compatibilities` dá os veículos informados pelo vendedor (`source: SELLER`, com `catalog_product_id`). Desde 15/07/2026, os do catálogo do ML (`source: CATALOGO`) vêm só com a marca e o `total`: entram só na contagem do relatório. Cada veículo é lido em `GET /products/{id}` (atributos `BRAND`, `MODEL`, `VEHICLE_YEAR` e `ENGINE`) e fica em cache por um dia, porque o catálogo é o mesmo para todas as lojas. Fontes: [compatibilidades](https://developers.mercadolivre.com.br/pt_br/compatibilidades-itens-e-produtos-de-autopecas) e [produtos](https://developers.mercadolivre.com.br/pt_br/buscador-de-produtos).
- **Correspondência (`ChannelCompatibilityRules`, regras fixas):** nunca grava veículo a mais. Marca e modelo precisam bater pelo nome; o modelo do ML pode ser o começo do nosso ("Gol" → "Gol G5"). A versão precisa cobrir o ano. Com motor, vale a única versão com a mesma cilindrada e mais palavras em comum (mesma regra do envio, agora em `VehicleNames`). Sem motor, só vale se o modelo tiver uma versão naquele ano. Anos seguidos da mesma versão viram uma faixa. O que não casa vai para o relatório pelo nome (até 10, "e mais N").
- **Falha do ML ao ler os veículos:** a peça entra sem eles, com aviso. Repetir o lote baixaria as fotos de novo.
- **Peça existente ligada ao anúncio:** não recebe veículos (a peça não muda).
- **HIPÓTESES:** até 200 veículos por anúncio; a lista vem inteira, sem paginação (a página não cita paginação).

## Consequências

- O saldo inicial vem do ML, com movimento de ajuste "importação do Mercado Livre" em nome de quem pediu.
- Se um lote cair depois de gravar fotos, os arquivos ficam sem registro. É um custo pequeno, preferível a baixar tudo de novo em cada tentativa.
- **Pendente:**
  - anúncios com variações;
  - medidas e peso da embalagem (`shipping.dimensions`);
  - confirmar a paginação com o app real.
