using Ecommerce.Application.Reports;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;
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

    private static readonly TenantStatus[] Eligible = [TenantStatus.Active, TenantStatus.ReadOnly];

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
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var owners = await (from t in db.Tenants.AsNoTracking()
                            join m in db.TenantMemberships.AsNoTracking() on t.Id equals m.TenantId
                            join u in db.UserAccounts.AsNoTracking() on m.UserId equals u.Id
                            where Eligible.Contains(t.Status) && m.Role == TenantRole.Owner
                            select new { TenantId = t.Id, u.Email }).ToListAsync(ct);
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        var tenants = owners.GroupBy(o => o.TenantId).ToList();
        foreach (var tenant in tenants)
            await bus.PublishAsync(new PrepareDailySummary(tenant.Select(o => new DailySummaryRecipient(o.Email)).ToList()),
                new DeliveryOptions { TenantId = tenant.Key.ToString() });
        LogScheduled(logger, tenants.Count);
        return tenants.Count;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Resumo diário conferido em {Tenants} lojas")]
    private static partial void LogScheduled(ILogger logger, int tenants);
}
