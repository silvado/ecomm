using Ecommerce.Infrastructure;
using Ecommerce.Infrastructure.Messaging;
using Wolverine;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
// Processa filas duráveis e mensagens agendadas (ex.: expiração de reservas — RF12) — ADR-0002.
builder.UseWolverine(opts => opts.ConfigureMessaging(builder.Configuration.GetConnectionString("Tenants")!));

var host = builder.Build();
host.Run();
