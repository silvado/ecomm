using Ecommerce.Infrastructure.Messaging;
using Ecommerce.Infrastructure.Integrations;
using Ecommerce.Application.Integrations;
using Ecommerce.Infrastructure.Catalog.Import;
using Ecommerce.Application.Catalog;
using Ecommerce.Application.Customers;
using Ecommerce.Application.Email;
using Ecommerce.Application.Identity;
using Ecommerce.Infrastructure.Customers;
using Ecommerce.Infrastructure.Email;
using Ecommerce.Application.Fiscal;
using Ecommerce.Infrastructure.Fiscal;
using Ecommerce.Infrastructure.Catalog;
using Ecommerce.Application.Orders;
using Ecommerce.Application.Payments;
using Ecommerce.Application.Shipping;
using Ecommerce.Application.Storage;
using Ecommerce.Application.Store;
using Ecommerce.Infrastructure.Orders;
using Ecommerce.Infrastructure.Payments;
using Ecommerce.Infrastructure.Shipping;
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
        // RF37: plano da loja (limites e recursos como dados).
        services.AddScoped<PlanDirectory>();
        services.AddScoped<Application.Platform.IPlanUsage, PlanUsageService>();
        services.AddScoped<IStoreProfileService, StoreProfileService>();
        services.AddScoped<IPartQueries, PartQueries>();
        services.AddScoped<IStoreCatalog, StoreCatalog>();

        // Frete (RF14): Melhor Envio da loja (token no cofre) ou fake em dev/testes.
        services.Configure<ShippingOptions>(configuration.GetSection(ShippingOptions.Section));
        services.AddHttpClient(ShippingProviderResolver.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));
        services.AddScoped<IShippingProviderResolver, ShippingProviderResolver>();
        services.AddScoped<IStorefrontCart, StorefrontCart>();
        services.AddScoped<IStorefrontCheckout, StorefrontCheckout>();
        services.AddScoped<IOrderQueries, OrderQueries>();
        services.Configure<PaymentOptions>(configuration.GetSection(PaymentOptions.Section));
        services.AddHttpClient(PaymentGatewayResolver.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15));
        services.AddScoped<PaymentGatewayResolver>();
        services.AddScoped<IPaymentGatewayResolver>(sp => sp.GetRequiredService<PaymentGatewayResolver>());
        services.AddScoped<IPaymentSettings, PaymentSettings>();
        services.AddScoped<IStorefrontPayment, StorefrontPayment>();
        services.AddScoped<IPaymentWebhook, PaymentWebhook>();
        services.AddScoped<IPartPhotos, PartPhotoService>();
        services.AddScoped<IPartImports, PartImportService>();
        services.AddScoped<IDonorVehicles, DonorVehicleService>();
        // RF28/RF29: log de integração (grava em escopo próprio), falhas e métricas da fila.
        services.AddScoped<IIntegrationLog, IntegrationLog>();
        services.AddScoped<IIntegrationQueries, IntegrationQueries>();
        // RF26: avisos de canais (inbox → loja). Canal simulado só em dev/testes; os reais entram no E2.
        services.AddScoped<ChannelAccountDirectory>();
        services.AddScoped<ChannelProcessors>();
        if (configuration.GetValue<bool>("Channels:UseFakeChannel"))
            services.AddScoped<IChannelEventProcessor, FakeChannelProcessor>();
        // E2: anúncios nos canais e propagação de estoque (RF24/RF25).
        services.AddScoped<Application.Channels.IChannelConnectorResolver, Channels.ChannelConnectorResolver>();
        services.AddScoped<Application.Channels.IChannelQueries, Channels.ChannelService>();
        services.AddScoped<Channels.ChannelService>();
        services.AddSingleton<Channels.ChannelSyncMetrics>();
        services.Configure<Channels.ReconciliationOptions>(configuration.GetSection(Channels.ReconciliationOptions.Section));
        services.AddScoped<QueueMetrics>();
        services.AddScoped<VehicleImporter>();
        services.AddScoped<IVehicleCatalog, VehicleCatalog>();

        // E-mail da loja ao comprador (RF16, Q38): SMTP da loja ou da plataforma; "memory" guarda as mensagens (testes).
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.Section));
        if (string.Equals(configuration[$"{EmailOptions.Section}:Provider"], "memory", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<InMemoryEmailOutbox>();
            services.AddScoped<IEmailSender, InMemoryEmailSender>();
        }
        else
        {
            services.AddScoped<IEmailSender, SmtpEmailSender>();
        }
        services.AddScoped<EmailBranding>();
        services.AddScoped<IStoreEmailSettings, StoreEmailSettingsService>();
        services.AddScoped<ICustomerArea, CustomerAreaService>();

        // NF-e (RF19): provedor da loja (Focus NFe na parte B) ou o falso em dev/testes.
        services.Configure<FiscalOptions>(configuration.GetSection(FiscalOptions.Section));
        services.AddScoped<IFiscalProviderResolver, FiscalProviderResolver>();
        services.AddScoped<IFiscalService, FiscalService>();
        services.AddScoped<TenantIssuer>();
        services.AddScoped<IPartCompatibilities, PartCompatibilityService>();

        services.Configure<VaultOptions>(configuration.GetSection(VaultOptions.Section));
        services.AddScoped<SecretVault>();
        services.AddScoped<ISecretVault, SecretVault>();
        services.AddScoped<IVaultPanel, VaultPanel>();

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
