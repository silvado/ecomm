using Ecommerce.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ecommerce.Integration.Tests.Tenancy;

/// <summary>
/// RNF01 — nenhuma consulta pode retornar ou alterar dados de outro tenant.
/// Prova as duas barreiras de forma independente (filtro EF e RLS) e a falha fechada sem tenant.
/// </summary>
public sealed class TenantIsolationTests(TenantDatabaseFixture db) : IClassFixture<TenantDatabaseFixture>
{
    [Fact]
    public async Task Com_filtro_EF_tenant_ve_somente_as_proprias_pecas()
    {
        await using var context = db.CreateAppContext(db.TenantA);

        var parts = await context.Parts.ToListAsync();

        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => Assert.Equal(db.TenantA, p.TenantId));
    }

    [Fact]
    public async Task Sem_filtro_EF_o_RLS_sozinho_bloqueia_outros_tenants()
    {
        await using var context = db.CreateAppContext(db.TenantB);

        var parts = await context.Parts.IgnoreQueryFilters().ToListAsync();

        var part = Assert.Single(parts);
        Assert.Equal(db.TenantB, part.TenantId);
    }

    [Fact]
    public async Task Sql_cru_tambem_e_filtrado_pelo_RLS()
    {
        await using var context = db.CreateAppContext(db.TenantB);

        var count = await context.Database
            .SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM parts")
            .SingleAsync();

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Sem_tenant_definido_nenhuma_linha_e_retornada()
    {
        await using var context = db.CreateAppContext(tenantId: null);

        var parts = await context.Parts.IgnoreQueryFilters().ToListAsync();

        Assert.Empty(parts);
    }

    [Fact]
    public async Task Update_e_delete_em_linhas_de_outro_tenant_nao_afetam_nada()
    {
        await using var context = db.CreateAppContext(db.TenantA);

        var updated = await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE parts SET title = 'invadido' WHERE tenant_id = {db.TenantB}");
        var deleted = await context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM parts WHERE tenant_id = {db.TenantB}");

        Assert.Equal(0, updated);
        Assert.Equal(0, deleted);
    }

    [Fact]
    public async Task RLS_rejeita_insert_com_tenant_diferente_do_contexto()
    {
        await using var context = db.CreateAppContext(db.TenantA);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO parts (id, tenant_id, internal_code, title, price) VALUES ({Guid.CreateVersion7()}, {db.TenantB}, 'X-1', 'intruso', 1)"));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState); // 42501: violação de política RLS
    }

    [Fact]
    public async Task SaveChanges_recusa_entidade_de_outro_tenant()
    {
        await using var context = db.CreateAppContext(db.TenantA);
        context.Parts.Add(new Part(db.TenantB, "X-2", "intruso", 1m));

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Conexao_reaproveitada_do_pool_nao_herda_o_tenant_anterior()
    {
        // Pool com 1 conexão: o segundo contexto obrigatoriamente reutiliza a conexão física do primeiro.
        await using (var first = db.CreateAppContext(db.TenantA))
        {
            Assert.NotEmpty(await first.Parts.ToListAsync());
        }

        // Conexão crua, sem o interceptor: só o reset do pool (DISCARD ALL) pode ter limpado a variável.
        await using var raw = new NpgsqlConnection(db.AppConnectionString);
        await raw.OpenAsync();
        await using var setting = new NpgsqlCommand("SELECT coalesce(current_setting('app.tenant_id', true), '')", raw);
        await using var count = new NpgsqlCommand("SELECT count(*)::int FROM parts", raw);

        Assert.Equal(string.Empty, await setting.ExecuteScalarAsync());
        Assert.Equal(0, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Toda_tabela_do_schema_public_tem_RLS_habilitado_e_forcado()
    {
        await using var context = db.CreateAppContext(db.TenantA);

        var unprotected = await context.Database.SqlQueryRaw<string>("""
            SELECT c.relname AS "Value"
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind = 'r'
              AND c.relname <> '__ef_migrations_history'
              AND NOT (c.relrowsecurity AND c.relforcerowsecurity)
            """).ToListAsync();

        Assert.Empty(unprotected);
    }

    [Fact]
    public async Task Papel_da_aplicacao_nao_ignora_RLS_nem_e_dono_das_tabelas()
    {
        await using var context = db.CreateAppContext(db.TenantA);

        var bypass = await context.Database.SqlQueryRaw<bool>(
            "SELECT (rolbypassrls OR rolsuper) AS \"Value\" FROM pg_roles WHERE rolname = current_user").SingleAsync();
        var ownsTables = await context.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM pg_tables WHERE schemaname = 'public' AND tableowner = current_user").SingleAsync();

        Assert.False(bypass);
        Assert.Equal(0, ownsTables);
    }
}
