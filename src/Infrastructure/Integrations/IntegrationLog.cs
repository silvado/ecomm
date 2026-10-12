using Ecommerce.Application.Common;
using Ecommerce.Application.Integrations;
using Ecommerce.Application.Tenancy;
using Ecommerce.Domain.Integrations;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecommerce.Infrastructure.Integrations;

/// <summary>
/// Log de integração (RF29) gravado num escopo próprio (outro DbContext, outra transação): a chamada fica registrada
/// mesmo se a operação de negócio for desfeita, e uma falha ao registrar nunca derruba a operação.
/// </summary>
public sealed partial class IntegrationLog(ITenantContext tenant, IServiceScopeFactory scopes, TimeProvider clock, ILogger<IntegrationLog> logger) : IIntegrationLog
{
    public async Task RecordAsync(string channel, string operation, bool success, TimeSpan duration, string? errorSummary, string? reference,
        Guid? orderId = null, Guid? partId = null, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tenantId) return;
        LogCall(logger, channel, operation, success, (int)duration.TotalMilliseconds);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
            var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
            db.IntegrationLogs.Add(new IntegrationLogEntry(tenantId, channel, operation, success, (int)duration.TotalMilliseconds,
                Redaction.Text(errorSummary, IntegrationLogEntry.ErrorMaxLength), reference, orderId, partId, Correlation.Current, clock.GetUtcNow()));
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception e) when (e is DbUpdateException or InvalidOperationException or OperationCanceledException or Npgsql.NpgsqlException)
        {
            LogNotRecorded(logger, e, channel, operation);
        }
    }

    public async Task LinkOrderAsync(Guid orderId, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tenantId || Correlation.Current is not { } correlation) return;
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
        await db.IntegrationLogs.Where(l => l.CorrelationId == correlation && l.OrderId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.OrderId, orderId), ct);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Integração {Channel} · {Operation}: sucesso={Success} em {DurationMs} ms")]
    private static partial void LogCall(ILogger logger, string channel, string operation, bool success, int durationMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Não foi possível registrar a chamada {Channel} · {Operation} no log de integração")]
    private static partial void LogNotRecorded(ILogger logger, Exception exception, string channel, string operation);
}
