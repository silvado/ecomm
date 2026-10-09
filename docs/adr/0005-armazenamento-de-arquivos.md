# ADR-0005 — Armazenamento de fotos e arquivos

- **Status:** Aceito
- **Data:** 2026-10-07
- **Requisitos:** RF08, RF19 (DANFE/XML), RF39, RNF07

## Contexto

Peças usadas têm muitas fotos (até 20 por peça, 3 tamanhos cada). O BRIEF não define storage. Fotos precisam de backup fora do servidor e URLs públicas para ML/Meta baixarem.

## Alternativas

| Opção | Prós | Contras |
|---|---|---|
| A. Disco local da VPS | Simples | Backup e migração manuais; enche o disco da VPS |
| **B. API S3 (`IFileStorage`): servidor S3 local em dev; provedor S3-compatível gerenciado em produção** | Backup/redundância do provedor, independe da VPS, CDN opcional | Custo mensal pequeno |
| C. Fotos no PostgreSQL | Tudo num lugar | Banco e backups enormes |

## Decisão

**Opção B.** Interface `IFileStorage` com implementação S3 (AWS SDK, endpoint configurável). Chaves no formato `{tenantId}/parts/{partId}/{photoId}-{size}.webp`. Bucket público só para fotos de produto; XML/DANFE e exportações em bucket privado com URL pré-assinada. Provedor de produção a definir (Cloudflare R2, Backblaze B2, Wasabi, Magalu Cloud…) — escolha por custo e região.

## Consequências

- SeaweedFS no `docker-compose` de dev (buckets `fotos` e `documentos`) — ver atualização abaixo.
- RNF07: o backup das fotos é responsabilidade do provedor + replicação para segundo bucket.

## Atualização (2026-10-09): implementação

- `IFileStorage` (Application) com `StorageArea.Public`/`Private` → buckets `fotos`/`documentos`. Adaptadores: `S3FileStorage` (AWS SDK for .NET v4, `ForcePathStyle`, credenciais em `Storage__AccessKey`/`Storage__SecretKey`) e `InMemoryFileStorage` (testes; `Storage__Provider=memory`). Contrato verificado contra o SeaweedFS real em `S3FileStorageTests`.
- Os checksums CRC que o SDK v4 envia por padrão funcionam com o SeaweedFS 4.47; ao escolher o provedor de produção, rodar `S3FileStorageTests` contra ele antes de trocar.
- Logo da loja (RF01 CA2): `{tenantId}/marca/logo-{id}` no bucket público, **servido pela API** em `/api/loja/logo/{id}` no domínio da loja (cache de 1 ano, id novo a cada troca, `nosniff` + CSP `sandbox`). CDN/URL direta do bucket fica para quando houver volume.

## Atualização (2026-10-09): fotos das peças (RF08 CA3)

- Conversão com **NetVips** (MIT) sobre a **libvips** (LGPL-2.1, vinculada dinamicamente: `libvips.so.42` vai como biblioteca separada no container). Escolhida pela memória baixa (processa em fluxo) e licença compatível com SaaS; ImageSharp descartado pela licença Split (pagamento acima de US$ 1 mi/ano).
- Upload até 10 MB e 40 MP (conferido pelo cabeçalho, antes de decodificar), JPEG/PNG/WebP pela assinatura do arquivo, `BlockUntrusted` (só decodificadores confiáveis), `failOn=error`, cache de operações desligado, no máximo 2 conversões simultâneas.
- Saída: WebP q=82 com o lado maior em **1600, 800 e 300 px** (sem ampliar), orientação EXIF aplicada e **todos os metadados removidos** (GPS de celular). Original guardado no bucket privado para gerar tamanhos novos no futuro.
- Chaves: `{tenantId}/pecas/{partId}/{photoId}-{tamanho}.webp` (público) e `{photoId}-original` (privado). No painel, servidas pela API com o token; a URL pública da loja vem com o catálogo da loja (RF13).
- Imagens Docker publicadas só com os binários nativos da plataforma do container (`-r linux-x64|arm64`).

## Atualização (2026-10-08): MinIO substituído por SeaweedFS em dev

A MinIO deixou de publicar as imagens Docker da edição comunitária (Docker Hub em out/2025, depois quay.io); o pull de `minio/minio` falha. Como só usamos a API S3, o servidor de dev é intercambiável.

Escolhido **SeaweedFS** (`chrislusf/seaweedfs`, Apache 2.0) em modo `weed mini`: nó único, credencial por `AWS_ACCESS_KEY_ID`/`AWS_SECRET_ACCESS_KEY`, buckets criados na subida por `S3_BUCKET`, API S3 na porta 8333. Verificado com o AWS CLI (upload e listagem com assinatura; requisição sem assinatura recebe 403).

Descartados: Garage (configuração inicial em dois passos, AGPL), RustFS (alfa), S3Proxy e Zenko CloudServer (viáveis, mas menos maduros como servidor de arquivos).

Fontes: [Quick Start with weed mini](https://github.com/seaweedfs/seaweedfs/wiki/Quick-Start-with-weed-mini), [S3 Credentials](https://github.com/seaweedfs/seaweedfs/wiki/S3-Credentials), [comparação de alternativas ao MinIO para S3 local (rmoff, jan/2026)](https://rmoff.net/2026/01/14/alternatives-to-minio-for-single-node-local-s3/), [kserve#4767](https://github.com/kserve/kserve/issues/4767).
