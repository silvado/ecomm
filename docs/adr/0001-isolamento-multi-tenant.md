# ADR-0001 — Isolamento multi-tenant

- **Status:** Aceito
- **Data:** 2026-10-07
- **Requisitos:** RNF01, RNF08, RF04, RF07

## Contexto

Todos os tenants compartilham o mesmo código. Vazamento de dados entre lojas é o pior defeito possível do produto (RNF01). Ao mesmo tempo, o início precisa caber numa VPS de 4 vCPU / 8 GB e um tenant grande deve poder ir para um banco próprio sem mudança de código (RNF08).

## Alternativas consideradas

| Opção | Prós | Contras |
|---|---|---|
| A. Banco único, `tenant_id` + filtro global do EF | Simples, barato | Um `IgnoreQueryFilters`, SQL cru ou bug de join vaza dados; sem segunda barreira |
| **B. Banco único, `tenant_id` + filtro EF + RLS no PostgreSQL** | Duas barreiras independentes; RLS vale até para SQL cru e relatórios; barato | Exige disciplina com papéis do banco e com a variável de sessão; pequeno custo de performance |
| C. Schema por tenant | Isolamento lógico forte | Migrações N vezes, catálogo cresce, ruim com pool de conexões, complexo para relatórios da plataforma |
| D. Banco por tenant | Isolamento máximo | Custo e operação inviáveis com dezenas de lojas pequenas numa VPS |

## Decisão

**Opção B**, com o caminho para D para tenants grandes:

1. **Catálogo de tenants** (banco `plataforma`, sem RLS): tenants, domínios, planos, assinaturas, string de conexão do banco de dados do tenant (por padrão o banco compartilhado `lojas`). Resolução de tenant (Host → tenant) consulta só o catálogo, com cache.
2. **Banco de dados do tenant** (`lojas`, compartilhado por padrão): toda tabela de negócio tem `tenant_id uuid NOT NULL`, índice começando por `tenant_id` e:
   ```sql
   ALTER TABLE part ENABLE ROW LEVEL SECURITY;
   ALTER TABLE part FORCE ROW LEVEL SECURITY;
   CREATE POLICY tenant_isolation ON part
     USING (tenant_id = current_setting('app.tenant_id', true)::uuid)
     WITH CHECK (tenant_id = current_setting('app.tenant_id', true)::uuid);
   ```
   Sem `app.tenant_id` definido, `current_setting(..., true)` retorna `NULL` e nenhuma linha passa — falha fechada.
3. **Papéis do PostgreSQL:**
   - `app_migrator`: dono das tabelas, usado só pelas migrações.
   - `app_user`: usado pela API e pelo Worker; `NOBYPASSRLS`, não é dono, só DML.
   - `app_platform`: acesso ao catálogo e operações de superadmin; leituras entre tenants só por funções/visões específicas e auditadas.
4. **Fixação do tenant na conexão:** um `DbConnectionInterceptor` executa `SELECT set_config('app.tenant_id', @tenantId, false)` ao abrir a conexão, a partir do `ITenantContext` do escopo. O Npgsql executa `DISCARD ALL` ao devolver a conexão ao pool (padrão; **não** usar `No Reset On Close=true`), o que limpa a variável. Um teste de integração verifica que uma conexão reaproveitada não carrega o tenant anterior.
5. **Filtro global do EF Core** por `TenantId` em todas as entidades `ITenantOwned`, e preenchimento automático de `TenantId` no `SaveChanges` (lança exceção se diferente do contexto).
6. **Worker/jobs:** cada mensagem carrega `tenant_id`; o handler abre o escopo do tenant antes de tocar no banco. Jobs que percorrem todos os tenants listam os tenants pelo catálogo e processam um por vez, cada um em seu escopo.
7. **Tabelas globais** (sem `tenant_id`, sem RLS, somente leitura para `app_user`): veículos (marca/modelo/ano/motorização), categorias de referência, planos.
8. **Tenant grande:** mudar a string de conexão do tenant no catálogo para um banco dedicado, migrar os dados dele (script de cópia filtrado por `tenant_id`). O RLS continua ativo lá — o código não muda.

## Consequências

- Teste obrigatório (CI) prova isolamento em três cenários: com filtro EF, sem filtro EF (só RLS) e sem variável de tenant.
- Toda nova tabela de negócio precisa de policy; uma migração de verificação falha o build se encontrar tabela no schema de negócio sem RLS habilitado.
- SQL cru é permitido, pois o RLS protege; ainda assim deve ser revisado.
- Índices compostos começando por `tenant_id` são necessários para a performance com RLS.

## Fontes

- PostgreSQL — Row Security Policies: https://www.postgresql.org/docs/current/ddl-rowsecurity.html
- Npgsql — connection pooling e reset de estado: https://www.npgsql.org/doc/connection-string-parameters.html (parâmetro `No Reset On Close`)
- EF Core — Global Query Filters: https://learn.microsoft.com/ef/core/querying/filters
