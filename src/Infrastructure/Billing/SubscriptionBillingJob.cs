using Ecommerce.Application.Billing;
using Ecommerce.Application.Tenancy;
using Microsoft.Extensions.Caching.Memory;
using Ecommerce.Domain.Billing;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Ecommerce.Infrastructure.Billing;

/// <param name="Started">Lojas publicadas antes da cobrança que ganharam assinatura (começa no dia da rodada).</param>
/// <param name="Invoices">Mensalidades geradas.</param>
/// <param name="Requeued">Avisos do gateway gravados e não processados, postos de novo na fila.</param>
/// <param name="Changes">Lojas que mudaram de situação pela régua de inadimplência.</param>
public sealed record SubscriptionBillingRun(int Started, int Invoices, int Requeued, IReadOnlyList<StandingChange> Changes);

/// <summary>
/// Cobrança da assinatura (RF38) a cada hora (tudo é idempotente): gera as mensalidades com antecedência, reenfileira
/// avisos do gateway que ficaram parados e aplica a régua de inadimplência. Roda só no Worker.
/// </summary>
public sealed partial class SubscriptionBillingJob(IServiceScopeFactory scopes, IOptions<BillingOptions> options, TimeProvider clock,
    ILogger<SubscriptionBillingJob> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(1);

    /// <summary>Aviso gravado há mais que isto sem processar: a publicação na fila se perdeu.</summary>
    public static readonly TimeSpan StaleEvent = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRunDelay, clock, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunAsync(stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    LogRunFailed(logger, e);
                }
                await Task.Delay(Interval, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Desligando.
        }
    }

    public async Task<SubscriptionBillingRun> RunAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var now = clock.GetUtcNow();
        var today = BillingCalendar.Today(clock);

        // Lojas já publicadas antes da cobrança existir (ou publicadas pelo superadmin/semente): a assinatura começa hoje.
        var unstarted = await db.Tenants.AsNoTracking()
            .Where(t => (t.Status == TenantStatus.Active || t.Status == TenantStatus.ReadOnly || t.Status == TenantStatus.Suspended)
                && !db.Subscriptions.Any(s => s.TenantId == t.Id))
            .Select(t => t.Id).ToListAsync(ct);
        foreach (var tenantId in unstarted) db.Subscriptions.Add(Subscription.Start(tenantId, today, now));
        await db.SaveChangesAsync(ct);

        // Loja em encerramento ou encerrada não gera mais mensalidade.
        var active = await (from s in db.Subscriptions
                            join t in db.Tenants on s.TenantId equals t.Id
                            join p in db.Plans on t.PlanCode equals p.Code
                            where t.Status != TenantStatus.Closing && t.Status != TenantStatus.Closed
                            select new { Subscription = s, Plan = p }).AsNoTracking().ToListAsync(ct);
        var until = today.AddDays(options.Value.InvoiceLeadDays);
        var invoices = 0;
        foreach (var row in active)
            invoices += await SubscriptionInvoicing.AddMissingAsync(db, row.Subscription, row.Plan, today, until, now, ct);
        await db.SaveChangesAsync(ct);

        var stale = await db.SubscriptionEvents.AsNoTracking()
            .Where(e => e.Status == SubscriptionEventStatus.Received && e.ReceivedAt < now - StaleEvent)
            .Select(e => e.Id).ToListAsync(ct);
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        foreach (var id in stale) await bus.PublishAsync(new ProcessSubscriptionEvent(id));

        // Régua de inadimplência (RF38 CA2): depois dos avisos, para um pagamento que acabou de entrar já contar.
        var changes = await BillingStanding.ApplyAsync(db, null, today, ct);
        await db.SaveChangesAsync(ct);
        await BillingStanding.AnnounceAsync(changes, scope.ServiceProvider.GetRequiredService<ITenantCatalog>(),
            scope.ServiceProvider.GetRequiredService<IMemoryCache>(), bus, logger);

        if (unstarted.Count + invoices + stale.Count > 0) LogRun(logger, unstarted.Count, invoices, stale.Count);
        return new SubscriptionBillingRun(unstarted.Count, invoices, stale.Count, changes);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Cobrança da assinatura: {Started} assinaturas iniciadas, {Invoices} mensalidades geradas, {Requeued} avisos reenfileirados")]
    private static partial void LogRun(ILogger logger, int started, int invoices, int requeued);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha na rodada da cobrança da assinatura")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);
}
