# Modelo de dados

Base: [ADR-0001](adr/0001-isolamento-multi-tenant.md). Nomes físicos em `snake_case`; ids `uuid` v7; datas `timestamptz` UTC; dinheiro `numeric(12,2)`.

## Dois bancos lógicos

| Banco | Conteúdo | RLS | Quem acessa |
|---|---|---|---|
| **`plataforma`** (catálogo) | tenants, domínios, identidade de usuários e vínculos, planos, assinaturas, cobrança da plataforma, catálogo de veículos (fonte) | Não — dados da plataforma | API (resolução de tenant, login), superadmin |
| **`lojas`** (dados de tenant) | tudo que pertence a uma loja | **Sim**, em toda tabela com `tenant_id` | API e Worker com `app_user` |

Um tenant grande pode ter seu próprio banco `lojas_<slug>` (RNF08): `tenant.database_key` aponta para a conexão. Por isso **não há chave estrangeira entre os bancos**; referências cruzadas são ids validados pela aplicação.

Tabelas de referência globais (veículos, categorias de referência) são **replicadas** do banco `plataforma` para o schema `ref` de cada banco `lojas` — hoje pela própria importação (`migrator importar-veiculos`, mesmos ids; `app_user` só tem SELECT em `ref`) — assim `part_compatibility` pode ter FK local para `ref.vehicle_version`.

## Banco `plataforma`

```mermaid
erDiagram
    TENANT ||--o{ TENANT_DOMAIN : "tem"
    TENANT ||--o{ TENANT_MEMBERSHIP : "tem"
    USER_ACCOUNT ||--o{ TENANT_MEMBERSHIP : "participa"
    PLAN ||--o{ PLAN_LIMIT : "define"
    PLAN ||--o{ PLAN_FEATURE : "habilita"
    TENANT ||--o{ SUBSCRIPTION : "assina"
    PLAN ||--o{ SUBSCRIPTION : ""
    SUBSCRIPTION ||--o{ PLATFORM_INVOICE : "gera"
    VEHICLE_BRAND ||--o{ VEHICLE_MODEL : ""
    VEHICLE_MODEL ||--o{ VEHICLE_VERSION : ""

    TENANT {
        uuid id PK
        string slug UK
        string cnpj UK
        string legal_name
        string trade_name
        string status "onboarding|active|read_only|suspended|closing|closed"
        string database_key "conexão do banco de dados do tenant"
        timestamptz created_at
    }
    TENANT_DOMAIN {
        uuid id PK
        uuid tenant_id FK
        string host UK "www.loja.com.br"
        string kind "platform_subdomain|custom_www|custom_apex"
        string verification_status "pending|verified|failed"
        timestamptz verified_at
        bool is_primary
    }
    USER_ACCOUNT {
        uuid id PK
        string email UK
        string password_hash
        bool mfa_enabled
        bool is_platform_admin
    }
    TENANT_MEMBERSHIP {
        uuid tenant_id PK
        uuid user_id PK
        string role "owner|operator"
    }
    PLAN {
        uuid id PK
        string code UK "essencial|profissional|completo"
        numeric monthly_price
        numeric setup_fee
    }
    PLAN_LIMIT {
        uuid plan_id PK
        string key PK "active_parts|users|ai_listings_month"
        int value
    }
    PLAN_FEATURE {
        uuid plan_id PK
        string feature PK "channel_ml|channel_instagram|channel_olx|ai_answers|..."
    }
    SUBSCRIPTION {
        uuid id PK
        uuid tenant_id FK
        uuid plan_id FK
        string status "trialing|active|past_due|canceled"
        date current_period_end
        string external_id "id no gateway da plataforma"
    }
    PLATFORM_INVOICE {
        uuid id PK
        uuid subscription_id FK
        string kind "monthly|setup|ai_overage"
        numeric amount
        date due_date
        string status
    }
    VEHICLE_BRAND {
        uuid id PK
        string name
    }
    VEHICLE_MODEL {
        uuid id PK
        uuid brand_id FK
        string name
    }
    VEHICLE_VERSION {
        uuid id PK
        uuid model_id FK
        int year_from
        int year_to
        string engine "1.6 8V Flex"
        string ml_reference "id equivalente no ML, se houver"
    }
```

Notas:
- Identidade fica no catálogo porque o login acontece **antes** de sabermos o tenant; um e-mail pode participar de mais de um tenant.
- `PLATFORM_AUDIT_LOG` (não desenhado) registra ações de superadmin (RF07 CA3).
- Implementado no RF07 (E1): `user_account` com `failed_login_count`, `locked_until` e `must_change_password` (sem `mfa_enabled`/`is_platform_admin` até o E4); `tenant.plan_code` → `plan.code` (plano vigente até existir `subscription`); `plan_limit` chaveado por `(plan_code, key)`; `refresh_token` (só o hash SHA-256, `family_id` para revogar a sessão inteira, `tenant_id` da loja escolhida, `used_at`/`revoked_at`). Tabelas com `tenant_id` neste banco não têm RLS: todo acesso filtra o tenant explicitamente e tem teste de isolamento.

## Banco `lojas` — todas as tabelas abaixo têm `tenant_id` + RLS

### Catálogo e estoque

```mermaid
erDiagram
    PART ||--o{ PART_PHOTO : ""
    PART ||--o{ PART_OEM_CODE : ""
    PART ||--o{ PART_COMPATIBILITY : ""
    DONOR_VEHICLE ||--o{ PART : "origem (peça usada)"
    PART ||--|| STOCK : ""
    PART ||--o{ STOCK_RESERVATION : ""
    PART ||--o{ STOCK_MOVEMENT : ""
    PART ||--o{ CHANNEL_LISTING : "anunciada em"
    CHANNEL_CONNECTION ||--o{ CHANNEL_LISTING : ""
    REF_VEHICLE_VERSION ||--o{ PART_COMPATIBILITY : ""

    PART {
        uuid id PK
        uuid tenant_id "RLS"
        string internal_code "UK (tenant_id, internal_code)"
        string title
        text description
        string condition "new|used|refurbished"
        numeric price
        int weight_g "embalagem; nulo = não informado"
        int length_cm
        int width_cm
        int height_cm
        string status "draft|active|inactive (sem exclusão)"
        jsonb fiscal "ncm, cfop, cst_csosn, origem"
        jsonb extra_attributes "atributos do ML etc."
        bool ai_generated_pending_review
        tsvector search_vector
        uuid donor_vehicle_id FK "RF10 — origem; só no painel"
        string traceability_tag "etiqueta de rastreabilidade; UK (tenant_id, tag) parcial"
        jsonb custom_fields "campos extras de rastreabilidade configurados na loja"
    }
    STOCK {
        uuid part_id PK
        uuid tenant_id "RLS"
        int on_hand "CHECK >= 0"
        int reserved "CHECK >= 0 AND reserved <= on_hand"
        int available "GENERATED on_hand - reserved"
        bigint version
    }
    STOCK_RESERVATION {
        uuid id PK
        uuid tenant_id "RLS"
        uuid part_id FK
        uuid order_id FK
        int quantity
        string status "active|converted|expired|released"
        timestamptz expires_at
    }
    STOCK_MOVEMENT {
        uuid id PK
        uuid tenant_id "RLS"
        uuid part_id FK
        int delta
        string reason "sale|counter_sale|cancel|return|adjust|import|reconcile"
        uuid order_id "nullable"
        uuid user_id "nullable"
        timestamptz at
    }
    PART_PHOTO {
        uuid id PK "também é a chave dos arquivos no storage"
        uuid tenant_id "RLS"
        uuid part_id FK
        int position "0 = capa; 0..19"
        string original_content_type
        timestamptz created_at
    }
    PART_OEM_CODE {
        uuid tenant_id "RLS"
        uuid part_id PK
        string code PK "só letras e dígitos, maiúsculo"
    }
    PART_COMPATIBILITY {
        uuid id PK
        uuid tenant_id "RLS"
        uuid part_id FK
        uuid vehicle_version_id FK
        string source "manual|ai_suggested|ml_import"
        bool confirmed
    }
    PART_IMPORT {
        uuid id PK
        uuid tenant_id "RLS — importação de peças por planilha (RF11)"
        uuid user_id "quem enviou; movimentos de estoque em nome dele"
        string file_name
        string status "validated|processing|completed"
        int total_rows "linhas de dados no arquivo"
        jsonb rows "linhas válidas já normalizadas, cadastradas em lotes"
        int error_count
        jsonb errors "linha + motivo (até 500 listados)"
        int existing_codes_at_preview "informativo"
        int processed_rows "progresso, gravado na transação de cada lote"
        int imported_count
        jsonb skipped "linha + código já cadastrado (pulado, Q33)"
        timestamptz created_at
        timestamptz started_at
        timestamptz finished_at
    }
    DONOR_VEHICLE {
        uuid id PK
        uuid tenant_id "RLS — veículo doador (RF10, Lei 12.977); só no painel"
        string plate "ABC1234 ou ABC1D23; UK (tenant_id, plate) parcial"
        string chassis "VIN 17; UK (tenant_id, chassis) parcial; CHECK placa ou chassi"
        string renavam "11 dígitos, DV conferido; UK parcial"
        string brand_model_year_color
        string deregistration_certificate "nº da certidão de baixa"
        string entry_invoice_key "chave da NF de entrada (44 dígitos, DV conferido)"
        date entry_date
        string notes
    }
    CHANNEL_CONNECTION {
        uuid id PK
        uuid tenant_id "RLS"
        string channel "mercado_livre|meta|olx"
        string external_account_id "user_id do vendedor no ML"
        string status "active|reauth_required|disabled"
        uuid secret_id FK "tokens no cofre"
    }
    CHANNEL_LISTING {
        uuid id PK
        uuid tenant_id "RLS"
        uuid part_id FK
        uuid connection_id FK
        string external_id "MLB123..."
        string status "pending|active|paused_by_system|paused_by_user|closed|error"
        int last_pushed_quantity
        numeric last_pushed_price
        timestamptz last_synced_at
    }
    REF_VEHICLE_VERSION {
        uuid id PK "réplica de plataforma.vehicle_version"
    }
```

**Regra de estoque (RNF02):** reserva e baixa são um único `UPDATE` condicional:
```sql
UPDATE stock SET reserved = reserved + @qty, version = version + 1
WHERE part_id = @part AND on_hand - reserved >= @qty;   -- 0 linhas = sem saldo
```
As `CHECK` constraints são a última barreira contra saldo negativo.

### Pedidos, pagamento e fiscal

```mermaid
erDiagram
    CUSTOMER ||--o{ CUSTOMER_ORDER : ""
    CUSTOMER_ORDER ||--|{ ORDER_ITEM : ""
    CUSTOMER_ORDER ||--o{ PAYMENT : ""
    CUSTOMER_ORDER ||--o{ SHIPMENT : ""
    CUSTOMER_ORDER ||--o{ FISCAL_DOCUMENT : ""
    CUSTOMER_ORDER ||--o{ STOCK_RESERVATION : ""
    CUSTOMER_ORDER ||--o{ ORDER_HISTORY : ""

    CUSTOMER {
        uuid id PK
        uuid tenant_id "RLS"
        string email "UK (tenant_id, email)"
        string name
        string document "CPF/CNPJ — só quando exigido pela NF-e"
        string phone
        jsonb addresses
        timestamptz anonymized_at "LGPD"
    }
    CUSTOMER_ORDER {
        uuid id PK
        uuid tenant_id "RLS"
        bigint number "UK (tenant_id, number) — sequencial por tenant (order_counters)"
        string origin "site|mercado_livre|olx|counter"
        string external_id "UK (tenant_id, origin, external_id) — canais (E2)"
        uuid customer_id FK "conta de cliente (RF16); convidado não tem"
        bytea access_token_hash "UK (tenant_id, hash) — link do comprador e idempotência do checkout e do balcão"
        string buyer_name_email_phone_cpf "cópia no pedido (convidado); no balcão, opcionais (vazios) e nome Consumidor final"
        string buyer_cnpj "cliente PJ no balcão (RF18); CNPJ pode ser alfanumérico"
        string payment_method "só balcão: cash|pix|debit_card|credit_card|other — CHECK (origin = counter) = (payment_method IS NOT NULL)"
        string delivery_method "shipping|pickup"
        string delivery_address "colunas delivery_* — só na entrega"
        string shipping_service "id, transportadora, serviço, prazo cotados"
        string pickup_address "cópia do endereço de retirada"
        string status "pending_payment|paid|needs_review|shipped|delivered|picked_up|canceled (invoiced/returned: RF19 e devoluções)"
        numeric items_total
        numeric shipping_total
        numeric total "CHECK = items_total + shipping_total"
        timestamptz placed_at
        timestamptz payment_deadline "fim da reserva; sem pagamento, cancela"
        timestamptz paid_at
        string review_reason "por que está em análise (needs_review)"
        string tracking_code "rastreio informado ao marcar enviado (RF17)"
        timestamptz shipped_at
        timestamptz delivered_at "entregue ou retirado"
        timestamptz canceled_at
        string canceled_by "payment_expired|store — pagamento atrasado só reativa o cancelado pelo prazo"
        string cancel_reason "motivo (obrigatório quando a loja cancela)"
    }
    ORDER_HISTORY {
        uuid id PK
        uuid tenant_id "RLS"
        uuid order_id FK
        string from_status "nulo na criação"
        string to_status
        string actor "buyer|user|payment|system"
        uuid user_id "quem fez, quando actor = user"
        string note "motivo, rastreio, razão da análise"
        timestamptz at
    }
    ORDER_ITEM {
        uuid id PK
        uuid tenant_id "RLS"
        uuid order_id FK
        uuid part_id FK
        uuid reservation_id FK "UK — reserva que segura a peça"
        int quantity
        numeric unit_price "preço do momento da compra"
        string title "cópia do título"
        string internal_code "cópia do código"
    }
    ORDER_COUNTER {
        uuid tenant_id PK "RLS"
        bigint last_number "UPSERT na transação do pedido"
    }
    PAYMENT {
        uuid id PK
        uuid tenant_id "RLS"
        uuid order_id FK
        string gateway "fake|mercado-pago|cielo"
        string external_id "UK (tenant_id, gateway, external_id)"
        string method "como o gateway informa (pix, cartão…)"
        string status "pending|approved|rejected|refunded|canceled — sempre consultado no gateway"
        numeric amount
        timestamptz updated_at
    }
    SHIPMENT {
        uuid id PK
        uuid tenant_id "RLS"
        uuid order_id FK
        string provider "melhor_envio|mercado_envios|pickup"
        string tracking_code
        string status
    }
    FISCAL_DOCUMENT {
        uuid id PK
        uuid tenant_id "RLS"
        uuid order_id FK
        string kind "nfe_sale|nfe_return"
        string access_key "chave de 44 dígitos"
        string status "processing|authorized|rejected|canceled"
        string xml_storage_key
        string danfe_storage_key
        string rejection_reason
    }
```

### Integração, IA, segurança e operação

```mermaid
erDiagram
    EXTERNAL_EVENT {
        uuid id PK
        uuid tenant_id "RLS — vem da URL do webhook (cada loja tem a sua)"
        string source "mercado_livre|mercado_pago|cielo|meta|olx_hub|fiscal"
        string idempotency_key "UK (tenant_id, source, idempotency_key)"
        string resource_id "recurso a consultar na origem"
        jsonb payload
        string status "received|processed|ignored|failed"
        timestamptz received_at
    }
    INTEGRATION_LOG {
        uuid id PK
        uuid tenant_id "RLS"
        string channel
        string operation "publish|update|pause|resume|reconcile|webhook"
        uuid part_id "nullable"
        uuid order_id "nullable"
        string status "ok|error|retrying"
        int duration_ms
        string error_summary "sem segredos"
        string correlation_id
        timestamptz at
    }
    TENANT_SECRET {
        uuid id PK
        uuid tenant_id "RLS"
        string kind "gateway_key|a1_certificate|oauth_token|..."
        bytea ciphertext
        bytea wrapped_data_key
        string key_version
        string hint "últimos 4 caracteres"
        timestamptz expires_at
    }
    BUYER_QUESTION {
        uuid id PK
        uuid tenant_id "RLS"
        string channel
        string external_id
        uuid part_id
        text question
        text answer
        string status "pending|draft_ready|answered_auto|answered_human"
    }
    AI_INTERACTION {
        uuid id PK
        uuid tenant_id "RLS"
        uuid user_id "nullable"
        string feature "part_from_photos|buyer_answer|daily_summary|sync_diagnosis|price_suggestion"
        string model
        int input_tokens
        int output_tokens
        numeric estimated_cost
        jsonb input_summary
        jsonb output
        timestamptz at
    }
    AI_USAGE_COUNTER {
        uuid tenant_id PK
        string feature PK
        date period PK "primeiro dia do mês"
        int used
    }
    STORE_BRANDING {
        uuid tenant_id PK
        string primary_color "#RRGGBB"
        string background_color
        string text_color
        string about "texto institucional"
        string return_policy "trocas e devoluções"
        string footer
        timestamptz updated_at
    }
    STORE_SETTINGS {
        uuid tenant_id PK
        int reservation_minutes "padrão 30"
        jsonb shipping "CEP origem, retirada"
        jsonb payment "gateway escolhido"
        bool dismantler_mode "RF10: modo desmontadora (Lei 12.977)"
        jsonb traceability_fields "campos extras: chave, rótulo, obrigatório"
    }
    ACCESS_LOG {
        uuid id PK
        uuid tenant_id "RLS"
        uuid user_id
        string action "LGPD: acesso a dado pessoal"
        string subject
        timestamptz at
    }
```

**Nota sobre `EXTERNAL_EVENT`:** o webhook chega antes de sabermos o tenant (ex.: ML identifica o vendedor por `user_id`). O endpoint grava o evento numa tabela de **entrada** sem RLS no schema `inbox` (só `INSERT` para `app_user`), resolve o tenant por `channel_connection.external_account_id` e enfileira o processamento já no escopo do tenant. A tabela com RLS guarda o evento depois de associado.

## Onde está o `tenant_id` — resumo

| Local | `tenant_id` | RLS |
|---|---|---|
| `plataforma.*` | Coluna em `tenant_domain`, `tenant_membership`, `subscription` | Não (acesso só pela API/superadmin) |
| `lojas.public.*` (negócio) | **Obrigatório, NOT NULL**, primeiro campo de todo índice | **Sim**, `FORCE` |
| `lojas.ref.*` (veículos, categorias) | Não tem | Não; somente leitura para `app_user` |
| `lojas.inbox.*` (webhooks crus) | Não tem | Não; `app_user` só `INSERT`/leitura pelo Worker de roteamento |
| `lojas.wolverine.*` (fila/outbox) | Dentro da mensagem | Não |
