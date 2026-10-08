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
| **B. API S3 (`IFileStorage`): MinIO em dev; provedor S3-compatível gerenciado em produção** | Backup/redundância do provedor, independe da VPS, CDN opcional | Custo mensal pequeno |
| C. Fotos no PostgreSQL | Tudo num lugar | Banco e backups enormes |

## Decisão

**Opção B.** Interface `IFileStorage` com implementação S3 (AWS SDK, endpoint configurável). Chaves no formato `{tenantId}/parts/{partId}/{photoId}-{size}.webp`. Bucket público só para fotos de produto; XML/DANFE e exportações em bucket privado com URL pré-assinada. Provedor de produção a definir (Cloudflare R2, Backblaze B2, Wasabi, Magalu Cloud…) — escolha por custo e região.

## Consequências

- MinIO no `docker-compose` de dev.
- RNF07: o backup das fotos é responsabilidade do provedor + replicação para segundo bucket.
