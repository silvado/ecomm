using Ecommerce.Infrastructure.Messaging;
using Ecommerce.Infrastructure.Platform;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ecommerce.Infrastructure.Persistence;

/// <summary>
/// Passo de implantação (ADR-0001/0002): aplica as migrações dos bancos <c>plataforma</c> e <c>lojas</c>, cria/atualiza
/// as tabelas do Wolverine e concede ao app_user acesso a elas. Roda com as credenciais do app_migrator, num host
/// configurado com <c>ConfigureMessaging(..., buildStorage: true)</c> e <b>sem</b> ser iniciado (não processa mensagens).
/// Idempotente: pode rodar a cada deploy.
/// </summary>
public static partial class DatabaseMigrator
{
    public static async Task RunAsync(IHost host, CancellationToken ct = default)
    {
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseMigrator));

        await using (var scope = host.Services.CreateAsyncScope())
        {
            LogStep(logger, "Aplicando migrações do banco plataforma");
            await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.MigrateAsync(ct);

            LogStep(logger, "Aplicando migrações do banco de tenants");
            await scope.ServiceProvider.GetRequiredService<TenantDbContext>().Database.MigrateAsync(ct);
        }

        LogStep(logger, "Criando/atualizando tabelas do Wolverine");
        await host.SetupResources(ct);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            // O app_migrator é dono do schema e das tabelas do Wolverine, então pode conceder o acesso.
            await scope.ServiceProvider.GetRequiredService<TenantDbContext>().Database
                .ExecuteSqlRawAsync(MessagingConfiguration.GrantPrivileges, ct);
        }

        LogStep(logger, "Migração concluída");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Migrador: {Step}")]
    private static partial void LogStep(ILogger logger, string step);
}
