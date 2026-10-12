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

var host = builder.Build();
host.Run();