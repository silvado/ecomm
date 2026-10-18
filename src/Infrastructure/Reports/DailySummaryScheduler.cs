using Ecommerce.Application.Reports;
using Ecommerce.Infrastructure.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace Ecommerce.Infrastructure.Reports;

/// <summary>
/// A cada 15 minutos, pede o resumo diário (RF34) a cada loja publicada e não suspensa, com os e-mails dos Donos. Cada
/// loja decide pelo próprio horário e envia uma vez por dia. Só no Worker.
/// </summary>
public sealed partial class DailySummaryScheduler(IServiceScopeFactory scopes, TimeProvider clock, ILogger<DailySummaryScheduler> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), clock, stoppingToken);
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
            await bus.PublishAsync(new PrepareDailySummary(owners), new DeliveryOptions { TenantId = tenantId.ToString() });
        LogScheduled(logger, tenants.Count);
        return tenants.Count;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Resumo diário conferido em {Tenants} lojas")]
    private static partial void LogScheduled(ILogger logger, int tenants);
}
