using Ecommerce.Application.Tenancy;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ecommerce.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var tenantsConnection = configuration.GetConnectionString("Tenants")
            ?? throw new InvalidOperationException("ConnectionStrings:Tenants não configurada.");

        services.AddScoped<TenantScope>();
        services.AddScoped<ITenantContext, TenantScopeAccessor>();
        // Opções singleton (recomendado pelo Wolverine): nada nelas depende do escopo; o tenant entra pelo construtor do contexto.
        services.AddDbContext<TenantDbContext>(
            options => ConfigureTenantDb(options, tenantsConnection),
            optionsLifetime: ServiceLifetime.Singleton);

        return services;
    }

    internal static void ConfigureTenantDb(DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention();
}
