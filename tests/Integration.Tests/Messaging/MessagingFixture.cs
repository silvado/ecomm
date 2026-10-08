using Ecommerce.Domain.Catalog;
using Ecommerce.Infrastructure;
using Ecommerce.Infrastructure.Messaging;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;
using Wolverine;

namespace Ecommerce.Integration.Tests.Messaging;

/// <summary>
/// Spike ADR-0002: Wolverine + EF Core + RLS com os papéis de produção.
/// Tabelas do Wolverine criadas pelo app_migrator; o host da aplicação roda como app_user, sem DDL.
/// </summary>
public sealed class MessagingFixture : IAsyncLifetime
{
    private const string Database = "lojas";
    private const string MigratorPassword = "migrator-test";
    private const string AppPassword = "app-test";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Guid TenantA { get; } = Guid.CreateVersion7();
    public Guid TenantB { get; } = Guid.CreateVersion7();
    public Guid PartA { get; private set; }
    public Guid PartB { get; private set; }

    public IHost Host { get; private set; } = null!;
    public HandlerProbe Probe => Host.Services.GetRequiredService<HandlerProbe>();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var superuser = _container.GetConnectionString();
        var tenantDbSuperuser = With(superuser, b => b.Database = Database);
        var migrator = With(tenantDbSuperuser, b => { b.Username = TenantDatabaseBootstrap.MigratorRole; b.Password = MigratorPassword; });
        var app = With(tenantDbSuperuser, b => { b.Username = TenantDatabaseBootstrap.AppRole; b.Password = AppPassword; });

        await ExecuteAsync(superuser, TenantDatabaseBootstrap.CreateRoles(MigratorPassword, AppPassword));
        await ExecuteAsync(superuser, TenantDatabaseBootstrap.CreateDatabase(Database));
        await ExecuteAsync(tenantDbSuperuser, TenantDatabaseBootstrap.GrantPrivileges);

        // 1) Migrações de negócio e tabelas do Wolverine, como app_migrator.
        using (var migrationHost = BuildHost(migrator, buildStorage: true))
        {
            await migrationHost.StartAsync();
            await using (var scope = migrationHost.Services.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<TenantDbContext>().Database.MigrateAsync();
            await migrationHost.StopAsync();
        }
        await ExecuteAsync(tenantDbSuperuser, MessagingConfiguration.GrantPrivileges);

        // 2) Host da aplicação, como app_user.
        Host = BuildHost(app, buildStorage: false);
        await Host.StartAsync();

        PartA = await SeedAsync(TenantA, "A-001");
        PartB = await SeedAsync(TenantB, "B-001");
    }

    public async Task DisposeAsync()
    {
        if (Host is not null) { await Host.StopAsync(); Host.Dispose(); }
        await _container.DisposeAsync();
    }

    private static IHost BuildHost(string connectionString, bool buildStorage)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Tenants"] = connectionString,
        });
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.AddSingleton<HandlerProbe>();
        builder.UseWolverine(opts =>
        {
            opts.ConfigureMessaging(connectionString, buildStorage);
            opts.Discovery.IncludeAssembly(typeof(MessagingFixture).Assembly);
        });
        return builder.Build();
    }

    private async Task<Guid> SeedAsync(Guid tenantId, string code)
    {
        await using var scope = Host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
        var part = new Part(tenantId, code, "Peça " + code, 100m);
        db.Parts.Add(part);
        await db.SaveChangesAsync();
        return part.Id;
    }

    private static string With(string connectionString, Action<NpgsqlConnectionStringBuilder> change)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        change(builder);
        return builder.ConnectionString;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
