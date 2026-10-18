using Ecommerce.Application.Reports;
using Ecommerce.Infrastructure.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace Ecommerce.Infrastructure.Reports;

/// <summary>
/// A cada 15 minutos, pede a cada loja publicada e não suspensa que confira a sincronização (RF33), com os e-mails dos
/// Donos. Só no Worker.
/// </summary>
public sealed partial class SyncGuardianScheduler(IServiceScopeFactory scopes, TimeProvider clock, ILogger<SyncGuardianScheduler> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), clock, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await ScheduleAllAsync(stoppingToken);
                await Task.Delay(Interval, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Desligando.
        }
    }

    /// <returns>Quantas lojas receberam o pedido.</returns>
    public async Task<int> ScheduleAllAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var tenants = await StoreOwners.LoadAsync(scope.ServiceProvider.GetRequiredService<PlatformDbContext>(), ct);
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        foreach (var (tenantId, owners) in tenants)
            await bus.PublishAsync(new CheckSyncHealth(owners), new DeliveryOptions { TenantId = tenantId.ToString() });
        LogScheduled(logger, tenants.Count);
        return tenants.Count;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sincronização conferida em {Tenants} lojas")]
    private static partial void LogScheduled(ILogger logger, int tenants);
}
