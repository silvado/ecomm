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
        // RF03: domínio próprio. DNS simulado só em dev/testes (Dns:UseFake).
        if (configuration.GetValue<bool>("Dns:UseFake")) services.AddSingleton<Application.Platform.IDnsResolver, FakeDnsResolver>();
        else services.AddSingleton<Application.Platform.IDnsResolver, DnsClientResolver>();
        services.AddScoped<DomainVerifier>();
        services.AddScoped<Application.Platform.ICustomDomains, CustomDomainService>();
        // RF07 CA3 / ADR-0007: superadmin com MFA e auditoria.
        services.AddScoped<Application.Platform.PlatformActor>();
        services.AddScoped<Application.Platform.IPlatformAudit, PlatformAudit>();
        services.AddSingleton<TotpSecretProtector>();
        services.AddScoped<Application.Platform.IPlatformAdminAuth, PlatformAdminAuth>();
        services.AddScoped<Application.Platform.IPlatformConsole, PlatformConsoleService>();
        services.AddSingleton<Application.Platform.ICustomContentSanitizer, CustomContentSanitizer>();
        services.AddScoped<Application.Platform.IPlatformThemeService, PlatformThemeService>();
        services.AddScoped<Application.Onboarding.IOnboardingService, Onboarding.OnboardingService>();
        // RF38: cobrança da assinatura pelo gateway da plataforma (Q05 em aberto: só o simulado, em dev/testes). Sem gateway,
        // a equipe registra os pagamentos à mão.
        services.Configure<Billing.BillingOptions>(configuration.GetSection(Billing.BillingOptions.Section));
        if (configuration.GetValue<bool>($"{Billing.BillingOptions.Section}:UseFakeGateway"))
            services.AddSingleton<Application.Billing.ISubscriptionBilling, Billing.FakeSubscriptionBilling>();
        services.AddScoped<Application.Billing.ISubscriptionPanel, Billing.SubscriptionPanel>();
        services.AddScoped<Application.Billing.ISubscriptionAdmin, Billing.SubscriptionAdmin>();
        services.AddScoped<Application.Billing.ISubscriptionWebhook, Billing.SubscriptionWebhook>();
        services.AddScoped<Billing.SubscriptionPayments>();

        // IA (ADR-0003, RF35/RF36): provedor + registro de toda chamada + limite do plano. Chave da plataforma só em variável de ambiente.
        services.Configure<Ai.AiOptions>(configuration.GetSection(Ai.AiOptions.Section));
        var aiOptions = configuration.GetSection(Ai.AiOptions.Section).Get<Ai.AiOptions>() ?? new Ai.AiOptions();
        // O provedor só existe com chave: quem pede IAiCompletion sempre recebe limite → registro → provedor.
        if (aiOptions.Provider == Ai.AiOptions.FakeProvider)
            services.AddKeyedSingleton<Application.Ai.IAiCompletion, Ai.FakeAiCompletion>(Ai.AiOptions.Section);
        else if (aiOptions.Provider == Ai.AiOptions.AnthropicProvider && !string.IsNullOrWhiteSpace(aiOptions.Anthropic.ApiKey))
        {
            services.AddSingleton(_ => new Anthropic.AnthropicClient(new Anthropic.Core.ClientOptions
            {
                ApiKey = aiOptions.Anthropic.ApiKey,
                BaseUrl = string.IsNullOrWhiteSpace(aiOptions.Anthropic.BaseUrl) ? "https://api.anthropic.com" : aiOptions.Anthropic.BaseUrl,
                MaxRetries = aiOptions.Anthropic.MaxRetries,
                Timeout = TimeSpan.FromSeconds(aiOptions.Anthropic.TimeoutSeconds),
            }));
            services.AddKeyedSingleton<Application.Ai.IAiCompletion, Ai.AnthropicAiCompletion>(Ai.AiOptions.Section);
        }
        else
            services.AddKeyedSingleton<Application.Ai.IAiCompletion, Ai.UnconfiguredAiCompletion>(Ai.AiOptions.Section);
        services.AddScoped<Application.Ai.IAiCompletion>(sp =>
        {
            var provider = sp.GetRequiredKeyedService<Application.Ai.IAiCompletion>(Ai.AiOptions.Section);
            var audited = ActivatorUtilities.CreateInstance<Ai.AuditedAiCompletion>(sp, provider);
            return ActivatorUtilities.CreateInstance<Ai.QuotaAiCompletion>(sp, (Application.Ai.IAiCompletion)audited);
        });
        services.AddScoped<Application.Ai.IAiUsage, Ai.AiUsageService>();
        // RNF04: registro de acesso a dados pessoais e anonimização do comprador.
        services.AddScoped<Application.Privacy.PanelActor>();
        services.AddScoped<Application.Privacy.IPersonalDataAccessLog, Privacy.PersonalDataAccessLog>();
        services.AddScoped<Application.Privacy.IBuyerAnonymization, Privacy.BuyerAnonymization>();
        services.AddScoped<Application.Privacy.IDataExports, Privacy.DataExportService>();
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
        services.AddScoped<IPartSuggestions, PartSuggestionService>();

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
