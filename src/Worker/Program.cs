using Ecommerce.Infrastructure;
using Ecommerce.Infrastructure.Integrations;
using Ecommerce.Infrastructure.Logging;
using Ecommerce.Infrastructure.Messaging;
using Serilog;
using Wolverine;

var builder = Host.CreateApplicationBuilder(args);

// Logs JSON com tenant, correlação e redação de dados sensíveis (RNF06).
// preserveStaticLogger: cada host usa o próprio logger (sem o Log.Logger estático, que vários hosts no mesmo processo —
// testes em paralelo — compartilhariam, misturando os logs de um no outro).
builder.Services.AddSerilog((services, logger) => logger.ConfigureEcommerce(builder.Configuration), preserveStaticLogger: true, writeToProviders: true);
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
// Avisos do Mercado Livre que não chegaram (RF21): de hora em hora, pelo missed_feeds.
builder.Services.AddHostedService<Ecommerce.Infrastructure.Channels.MercadoLivre.MercadoLivreMissedFeedsJob>();
// RF03: verificação dos domínios próprios a cada 5 min.
builder.Services.AddHostedService<Ecommerce.Infrastructure.Platform.DomainVerificationJob>();
// RF38: mensalidades da assinatura (a cada hora, idempotente).
builder.Services.AddHostedService<Ecommerce.Infrastructure.Billing.SubscriptionBillingJob>();
// RF34: resumo diário por e-mail aos Donos (confere a cada 15 min; cada loja no seu horário, uma vez por dia).
builder.Services.AddHostedService<Ecommerce.Infrastructure.Reports.DailySummaryScheduler>();
builder.Services.AddHostedService<Ecommerce.Infrastructure.Reports.SyncGuardianScheduler>();

var host = builder.Build();
host.Run();