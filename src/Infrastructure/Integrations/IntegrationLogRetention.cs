using Ecommerce.Domain.Integrations;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Platform;
using Ecommerce.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ecommerce.Infrastructure.Integrations;

/// <summary>
/// Retenção do log de integração (RF29 CA2): uma vez por dia apaga, loja a loja (o RLS só deixa ver uma por vez), o que
/// passou de 90 dias, e também as falhas já reprocessadas há mais de 90 dias. Roda só no Worker.
/// </summary>
public sealed partial class IntegrationLogRetention(IServiceScopeFactory scopes, TimeProvider clock, ILogger<IntegrationLogRetention> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRunDelay, clock, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await PurgeAsync(stoppingToken);
                await Task.Delay(Interval, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Desligando.
        }
    }

    /// <summary>Apaga o que passou da retenção em todas as lojas; devolve quantos registros saíram.</summary>
    public async Task<int> PurgeAsync(CancellationToken ct)
    {
        var limit = clock.GetUtcNow() - IntegrationLogEntry.Retention;
        List<Guid> tenants;
        await using (var scope = scopes.CreateAsyncScope())
            tenants = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Tenants.AsNoTracking().Select(t => t.Id).ToListAsync(ct);

        var removed = 0;
        foreach (var tenantId in tenants)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
                var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
                removed += await db.IntegrationLogs.Where(l => l.At < limit).ExecuteDeleteAsync(ct);
                removed += await db.MessageFailures.Where(f => f.Status == MessageFailureStatus.Replayed && f.ReplayedAt < limit).ExecuteDeleteAsync(ct);
            }
            catch (Exception e) when (e is DbUpdateException or Npgsql.NpgsqlException or InvalidOperationException)
            {
                LogPurgeFailed(logger, e, tenantId);
            }
        }
        LogPurged(logger, removed, tenants.Count);
        return removed;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Retenção do log de integração: {Removed} registros apagados em {Tenants} lojas")]
    private static partial void LogPurged(ILogger logger, int removed, int tenants);

    [LoggerMessage(Level = LogLevel.Error, Message = "Retenção do log de integração falhou na loja {TenantId}")]
    private static partial void LogPurgeFailed(ILogger logger, Exception exception, Guid tenantId);
}
