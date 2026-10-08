using Ecommerce.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
// Handlers e jobs entram com o spike do Wolverine (ADR-0002).

var host = builder.Build();
host.Run();
