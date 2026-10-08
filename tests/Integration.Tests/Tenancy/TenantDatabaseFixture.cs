using Ecommerce.Infrastructure;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Ecommerce.Integration.Tests.Tenancy;

/// <summary>
/// PostgreSQL real (Testcontainers) com os mesmos papéis da produção: migrações como app_migrator, testes como app_user.
/// </summary>
public sealed class TenantDatabaseFixture : IAsyncLifetime
{
    private const string Database = "lojas";
    private const string MigratorPassword = "migrator-test";
    private const string AppPassword = "app-test";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .Build();

    public Guid TenantA { get; } = Guid.CreateVersion7();
    public Guid TenantB { get; } = Guid.CreateVersion7();

    /// <summary>Conexão do app_user, com pool de 1 conexão para forçar reaproveitamento.</summary>
    public string AppConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var superuser = new NpgsqlConnectionStringBuilder(_container.GetConnectionString());

        await ExecuteAsync(superuser.ConnectionString,
            TenantDatabaseBootstrap.CreateRoles(MigratorPassword, AppPassword));
        await ExecuteAsync(superuser.ConnectionString, TenantDatabaseBootstrap.CreateDatabase(Database));
        await ExecuteAsync(new NpgsqlConnectionStringBuilder(superuser.ConnectionString) { Database = Database }.ConnectionString,
            TenantDatabaseBootstrap.GrantPrivileges);

        var migrator = new NpgsqlConnectionStringBuilder(superuser.ConnectionString)
        {
            Database = Database,
            Username = TenantDatabaseBootstrap.MigratorRole,
            Password = MigratorPassword,
        };
        await using (var migrationContext = CreateContext(migrator.ConnectionString, tenantId: null))
        {
            await migrationContext.Database.MigrateAsync();
        }

        AppConnectionString = new NpgsqlConnectionStringBuilder(superuser.ConnectionString)
        {
            Database = Database,
            Username = TenantDatabaseBootstrap.AppRole,
            Password = AppPassword,
            MaxPoolSize = 1,
        }.ConnectionString;

        await SeedAsync(TenantA, "A-001", "Farol Gol G5 esquerdo");
        await SeedAsync(TenantA, "A-002", "Retrovisor Onix direito");
        await SeedAsync(TenantB, "B-001", "Para-choque Civic 2015");
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public TenantDbContext CreateAppContext(Guid? tenantId) => CreateContext(AppConnectionString, tenantId);

    private static TenantDbContext CreateContext(string connectionString, Guid? tenantId)
    {
        var tenant = new TenantContext();
        if (tenantId is { } id) tenant.Set(id);

        var options = new DbContextOptionsBuilder<TenantDbContext>();
        DependencyInjection.ConfigureTenantDb(options, connectionString);
        options.AddInterceptors(new TenantConnectionInterceptor(tenant));
        return new TenantDbContext(options.Options, tenant);
    }

    private async Task SeedAsync(Guid tenantId, string code, string title)
    {
        await using var context = CreateAppContext(tenantId);
        context.Parts.Add(new Domain.Catalog.Part(tenantId, code, title, 100m));
        await context.SaveChangesAsync();
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
