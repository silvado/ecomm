using Ecommerce.Infrastructure;
using Ecommerce.Infrastructure.Messaging;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Migrator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;

// Migrador (passo de deploy): conecta como app_migrator via ConnectionStrings__Platform/ConnectionStrings__Tenants.
// Com "criar-loja ..." vira a ferramenta de criar lojas no servidor (sem migrar).
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddInfrastructure(builder.Configuration);
builder.UseWolverine(opts => opts.ConfigureMessaging(builder.Configuration.GetConnectionString("Tenants")!, buildStorage: true));

using var host = builder.Build();

if (args.Length > 0 && args[0] == CreateStoreCommand.Name)
    return await CreateStoreCommand.RunAsync(host, args[1..], Console.Out);

await DatabaseMigrator.RunAsync(host);
await DevSeed.RunAsync(host);
return 0;
