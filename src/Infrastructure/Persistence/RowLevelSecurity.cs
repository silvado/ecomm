using Ecommerce.Infrastructure.Tenancy;

namespace Ecommerce.Infrastructure.Persistence;

/// <summary>
/// SQL de RLS usado pelas migrações. Toda tabela de negócio nova deve chamar <see cref="EnableFor"/> na sua migração.
/// </summary>
public static class RowLevelSecurity
{
    public static string EnableFor(string table) => $"""
        ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;
        ALTER TABLE {table} FORCE ROW LEVEL SECURITY;
        CREATE POLICY tenant_isolation ON {table}
            USING (tenant_id = NULLIF(current_setting('{TenantConnectionInterceptor.SettingName}', true), '')::uuid)
            WITH CHECK (tenant_id = NULLIF(current_setting('{TenantConnectionInterceptor.SettingName}', true), '')::uuid);
        """;

    public static string DisableFor(string table) => $"""
        DROP POLICY IF EXISTS tenant_isolation ON {table};
        ALTER TABLE {table} NO FORCE ROW LEVEL SECURITY;
        ALTER TABLE {table} DISABLE ROW LEVEL SECURITY;
        """;
}
