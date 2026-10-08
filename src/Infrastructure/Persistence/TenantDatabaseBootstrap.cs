namespace Ecommerce.Infrastructure.Persistence;

/// <summary>
/// SQL de criação de papéis e permissões do banco de dados de tenant (ADR-0001).
/// Executado como superusuário antes das migrações. Espelhado em deploy/postgres/init para o ambiente Docker.
/// </summary>
public static class TenantDatabaseBootstrap
{
    public const string MigratorRole = "app_migrator";
    public const string AppRole = "app_user";

    /// <summary>Executar no banco <c>postgres</c>.</summary>
    public static string CreateRoles(string migratorPassword, string appPassword) => $"""
        CREATE ROLE {MigratorRole} LOGIN NOBYPASSRLS PASSWORD '{migratorPassword}';
        CREATE ROLE {AppRole} LOGIN NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD '{appPassword}';
        """;

    /// <summary>Executar no banco <c>postgres</c>, em comando separado (CREATE DATABASE não roda em lote).</summary>
    public static string CreateDatabase(string database) => $"CREATE DATABASE {database} OWNER {MigratorRole};";

    /// <summary>Executar dentro do banco de tenant, como superusuário.</summary>
    public const string GrantPrivileges = $"""
        REVOKE ALL ON SCHEMA public FROM PUBLIC;
        GRANT USAGE ON SCHEMA public TO {AppRole};
        ALTER DEFAULT PRIVILEGES FOR ROLE {MigratorRole} IN SCHEMA public
            GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {AppRole};
        ALTER DEFAULT PRIVILEGES FOR ROLE {MigratorRole} IN SCHEMA public
            GRANT USAGE, SELECT ON SEQUENCES TO {AppRole};
        """;
}
