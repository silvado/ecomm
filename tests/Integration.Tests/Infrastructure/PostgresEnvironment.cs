using Ecommerce.Infrastructure;
using Ecommerce.Infrastructure.Messaging;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Platform;
using Ecommerce.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;
using Wolverine;

namespace Ecommerce.Integration.Tests.Infrastructure;

/// <summary>
/// PostgreSQL real (Testcontainers) montado como em produção (ADR-0001):
/// bancos <c>plataforma</c> e <c>lojas</c>, migrações e tabelas do Wolverine criadas pelo app_migrator,
/// aplicação conectando como app_user (sem BYPASSRLS, sem DDL).
/// </summary>
public sealed class PostgresEnvironment : IAsyncDisposable
{
    public const string PlatformDatabase = "plataforma";
    public const string TenantsDatabase = "lojas";
    private const string MigratorPassword = "migrator-test";
    private const string AppPassword = "app-test";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    private PostgresEnvironment() { }

    public string SuperuserConnectionString { get; private set; } = string.Empty;

    /// <summary>Conexões do app_user.</summary>
    public string PlatformConnectionString { get; private set; } = string.Empty;
    public string TenantsConnectionString { get; private set; } = string.Empty;

    public static async Task<PostgresEnvironment> StartAsync()
    {
        var env = new PostgresEnvironment();
        await env.InitializeAsync();
        return env;
    }

    /// <summary>Configuração mínima para <see cref="DependencyInjection.AddInfrastructure"/>.</summary>
    public Dictionary<string, string?> AppConfiguration(Action<NpgsqlConnectionStringBuilder>? tenantsConnection = null) => new()
    {
        ["ConnectionStrings:Platform"] = PlatformConnectionString,
        ["ConnectionStrings:Tenants"] = With(TenantsConnectionString, tenantsConnection ?? (_ => { })),
        ["Vault:CurrentMasterKeyVersion"] = "v1",
        ["Vault:MasterKeys:v1"] = MasterKeyV1,
        ["Platform:Domain"] = "plataforma.test",
    };

    /// <summary>Chave mestra do cofre gerada por ambiente de teste (nunca uma chave fixa no código).</summary>
    public string MasterKeyV1 { get; } = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    public static string With(string connectionString, Action<NpgsqlConnectionStringBuilder> change)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        change(builder);
        return builder.ConnectionString;
    }

    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    private async Task InitializeAsync()
    {
        await _container.StartAsync();
        SuperuserConnectionString = _container.GetConnectionString();

        await ExecuteAsync(SuperuserConnectionString, TenantDatabaseBootstrap.CreateRoles(MigratorPassword, AppPassword));
        foreach (var database in new[] { PlatformDatabase, TenantsDatabase })
        {
            await ExecuteAsync(SuperuserConnectionString, TenantDatabaseBootstrap.CreateDatabase(database));
            await ExecuteAsync(Superuser(database), TenantDatabaseBootstrap.GrantPrivileges);
        }

        PlatformConnectionString = AppUser(PlatformDatabase);
        TenantsConnectionString = AppUser(TenantsDatabase);

        await RunMigratorAsync();
    }

    /// <summary>Mesmo passo do deploy (src/Migrator): migrações + tabelas do Wolverine + permissões, como app_migrator.</summary>
    public async Task RunMigratorAsync()
    {
        var migrationConfig = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Platform"] = Migrator(PlatformDatabase),
            ["ConnectionStrings:Tenants"] = Migrator(TenantsDatabase),
        };
        using var migrationHost = BuildHost(migrationConfig, Migrator(TenantsDatabase), buildMessageStorage: true);
        await DatabaseMigrator.RunAsync(migrationHost);
    }

    /// <summary>Host genérico com infraestrutura + Wolverine (sem handlers além dos do assembly de testes).</summary>
    public static IHost BuildHost(
        Dictionary<string, string?> configuration,
        string messagingConnection,
        bool buildMessageStorage = false,
        Action<IServiceCollection>? services = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Services.AddInfrastructure(builder.Configuration);
        services?.Invoke(builder.Services);
        builder.UseWolverine(opts =>
        {
            opts.ConfigureMessaging(messagingConnection, buildMessageStorage);
            opts.Discovery.IncludeAssembly(typeof(PostgresEnvironment).Assembly);
        });
        return builder.Build();
    }

    private string Superuser(string database) => With(SuperuserConnectionString, b => b.Database = database);

    private string Migrator(string database) => With(SuperuserConnectionString, b =>
    {
        b.Database = database;
        b.Username = TenantDatabaseBootstrap.MigratorRole;
        b.Password = MigratorPassword;
    });

    private string AppUser(string database) => With(SuperuserConnectionString, b =>
    {
        b.Database = database;
        b.Username = TenantDatabaseBootstrap.AppRole;
        b.Password = AppPassword;
    });

    /// <summary>Cria um <see cref="TenantDbContext"/> fora de DI, com o escopo de tenant informado.</summary>
    public static TenantDbContext CreateTenantContext(string connectionString, TenantScope scope)
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>();
        DependencyInjection.ConfigureTenantDb(options, connectionString);
        return new TenantDbContext(options.Options, scope);
    }
}
