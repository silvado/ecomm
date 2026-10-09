using Ecommerce.Application.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ecommerce.Migrator;

/// <summary>
/// Só desenvolvimento: cria a loja <c>demo</c> com um Dono de senha conhecida, pelo mesmo caso de uso do comando
/// <c>criar-loja</c>. Ligado apenas se <c>DevSeed__OwnerEmail</c> e <c>DevSeed__OwnerPassword</c> existirem.
/// Idempotente: se a loja já existe, não faz nada.
/// </summary>
internal static partial class DevSeed
{
    public static async Task RunAsync(IHost host)
    {
        var config = host.Services.GetRequiredService<IConfiguration>();
        var email = config["DevSeed:OwnerEmail"];
        var password = config["DevSeed:OwnerPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)) return;

        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DevSeed));
        await using var scope = host.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ITenantProvisioning>().CreateAsync(
            new CreateTenantRequest("demo", "11222333000181", "Loja Demo Ltda", "Loja Demo", email, password));

        if (result.Tenant is { } created) LogSeeded(logger, created.Host, created.OwnerId);
        else if (result.Error is not (CreateTenantError.SlugTaken or CreateTenantError.CnpjTaken)) LogSkipped(logger, result.Error!.Value);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "DevSeed: loja {Host} criada com o Dono {UserId}.")]
    private static partial void LogSeeded(ILogger logger, string host, Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DevSeed: semente ignorada ({Error}).")]
    private static partial void LogSkipped(ILogger logger, CreateTenantError error);
}
