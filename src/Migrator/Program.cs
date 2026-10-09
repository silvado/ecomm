using Ecommerce.Infrastructure;
using Ecommerce.Migrator;
using Ecommerce.Infrastructure.Messaging;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;

// Migrador (passo de deploy): conecta como app_migrator via ConnectionStrings__Platform/ConnectionStrings__Tenants.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddInfrastructure(builder.Configuration);
builder.UseWolverine(opts => opts.ConfigureMessaging(builder.Configuration.GetConnectionString("Tenants")!, buildStorage: true));

using var host = builder.Build();
await DatabaseMigrator.RunAsync(host);
await DevSeed.RunAsync(host);
