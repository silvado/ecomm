using Ecommerce.Application.Catalog;
using Ecommerce.Application.Identity;
using Ecommerce.Infrastructure.Catalog;
using Ecommerce.Application.Storage;
using Ecommerce.Application.Store;
using Ecommerce.Infrastructure.Storage;
using Ecommerce.Infrastructure.Store;
using Ecommerce.Application.Tenancy;
using Ecommerce.Application.Vehicles;
using Ecommerce.Infrastructure.Identity;
using Ecommerce.Application.Vault;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Platform;
using Ecommerce.Infrastructure.Tenancy;
using Ecommerce.Infrastructure.Vault;
using Ecommerce.Infrastructure.Vehicles;
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

        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.Section));
        if (string.Equals(configuration[$"{StorageOptions.Section}:Provider"], "memory", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<InMemoryFileStorage>();
            services.AddSingleton<IFileStorage>(sp => sp.GetRequiredService<InMemoryFileStorage>());
        }
        else
        {
            services.AddSingleton<IFileStorage, S3FileStorage>();
        }

        services.Configure<PlatformOptions>(configuration.GetSection(PlatformOptions.Section));
        services.AddScoped<ITenantProvisioning, TenantProvisioning>();
        services.AddScoped<IStoreProfileService, StoreProfileService>();
        services.AddScoped<IPartQueries, PartQueries>();
        services.AddScoped<IStoreCatalog, StoreCatalog>();
        services.AddScoped<IPartPhotos, PartPhotoService>();
        services.AddScoped<VehicleImporter>();
        services.AddScoped<IVehicleCatalog, VehicleCatalog>();
        services.AddScoped<IPartCompatibilities, PartCompatibilityService>();

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
