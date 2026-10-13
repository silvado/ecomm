using Ecommerce.Infrastructure;
using Ecommerce.Infrastructure.Integrations;
using Ecommerce.Infrastructure.Logging;
using Ecommerce.Infrastructure.Messaging;
using Serilog;
using Wolverine;

var builder = Host.CreateApplicationBuilder(args);

// Logs JSON com tenant, correlação e redação de dados sensíveis (RNF06).
builder.Services.AddSerilog((services, logger) => logger.ConfigureEcommerce(builder.Configuration), writeToProviders: true);
builder.Services.AddInfrastructure(builder.Configuration);
// Processa filas duráveis e mensagens agendadas (ex.: expiração de reservas — RF12) — ADR-0002.
builder.UseWolverine(opts => opts.ConfigureMessaging(builder.Configuration.GetConnectionString("Tenants")!,
    retryDelays: MessagingSettings.RetryDelays(builder.Configuration)));
// RF29 CA2: retenção de 90 dias do log de integração (uma instância só: o Worker).
builder.Services.AddHostedService<IntegrationLogRetention>();
// RF06 CA5: aviso no log 30, 15 e 5 dias antes de o certificado A1 vencer.
builder.Services.AddHostedService<Ecommerce.Infrastructure.Vault.CertificateExpiryMonitor>();
// RF27: reconciliação horária dos anúncios com os canais.
builder.Services.AddHostedService<Ecommerce.Infrastructure.Channels.ChannelReconciliationScheduler>();

var host = builder.Build();
host.Run();