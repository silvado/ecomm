using Ecommerce.Application.Identity;
using Ecommerce.Application.Tenancy;
using Ecommerce.Infrastructure.Identity;
using Ecommerce.Application.Vault;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Platform;
using Ecommerce.Infrastructure.Tenancy;
using Ecommerce.Infrastructure.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecommerce.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var tenantsConnection = configuration.GetConnectionString("Tenants")
            ?? throw new InvalidOperationException("ConnectionStrings:Tenants não configurada.");
        var platformConnection = configuration.GetConnectionString("Platform")
            ?? throw new InvalidOperationException("ConnectionStrings:Platform não configurada.");

        services.AddScoped<TenantScope>();
        services.AddScoped<ITenantContext, TenantScopeAccessor>();
        // Opções singleton (recomendado pelo Wolverine): nada nelas depende do escopo; o tenant entra pelo construtor do contexto.
        services.AddDbContext<TenantDbContext>(
            options => ConfigureTenantDb(options, tenantsConnection),
            optionsLifetime: ServiceLifetime.Singleton);

        services.AddDbContext<PlatformDbContext>(
            options => ConfigurePlatformDb(options, platformConnection),
            optionsLifetime: ServiceLifetime.Singleton);
        services.AddMemoryCache();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ITenantCatalog, CachedTenantCatalog>();

        services.AddSingleton<PasswordHashing>();
        services.AddSingleton<IMembershipLookup, CachedMembershipLookup>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserAdministration, UserAdministration>();

        services.Configure<VaultOptions>(configuration.GetSection(VaultOptions.Section));
        services.AddScoped<SecretVault>();
        services.AddScoped<ISecretVault, SecretVault>();

        return services;
    }

    internal static void ConfigureTenantDb(DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention();

    internal static void ConfigurePlatformDb(DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention();
}
