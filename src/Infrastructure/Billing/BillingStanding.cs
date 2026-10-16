using Ecommerce.Application.Channels;
using Ecommerce.Application.Tenancy;
using Ecommerce.Domain.Billing;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Platform;
using Ecommerce.Infrastructure.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace Ecommerce.Infrastructure.Billing;

/// <param name="From">Situação antes da régua.</param>
/// <param name="To">Situação depois.</param>
public sealed record StandingChange(Guid TenantId, string Slug, TenantStatus From, TenantStatus To);

/// <summary>
/// Régua de inadimplência (RF38 CA2/CA3): leva a situação de cada loja ao estágio que as faturas vencidas pedem. Roda no job
/// de hora em hora e logo depois de qualquer pagamento ou cancelamento (a volta é automática). Loja suspensa pela equipe,
/// em implantação ou encerrada não é tocada (<see cref="Tenant.ApplyBillingStage"/>).
/// </summary>
public static partial class BillingStanding
{
    /// <summary>Aplica a régua (sem salvar). <paramref name="tenantIds"/> nulo = todas as lojas.</summary>
    public static async Task<IReadOnlyList<StandingChange>> ApplyAsync(PlatformDbContext db, IReadOnlyCollection<Guid>? tenantIds, DateOnly today,
        CancellationToken ct)
    {
        var query = db.Tenants.Where(t => t.Status == TenantStatus.Active || t.BillingHold);
        if (tenantIds is not null) query = query.Where(t => tenantIds.Contains(t.Id));
        var tenants = await query.ToListAsync(ct);
        if (tenants.Count == 0) return [];

        var ids = tenants.Select(t => t.Id).ToList();
        var oldest = await db.SubscriptionInvoices.AsNoTracking()
            .Where(i => ids.Contains(i.TenantId) && i.Status == InvoiceStatus.Open && i.DueDate < today)
            .GroupBy(i => i.TenantId)
            .Select(g => new { TenantId = g.Key, Due = g.Min(i => i.DueDate) })
            .ToDictionaryAsync(g => g.TenantId, g => g.Due, ct);

        var changes = new List<StandingChange>();
        foreach (var tenant in tenants)
        {
            var from = tenant.Status;
            var stage = DunningPolicy.StageFor(oldest.TryGetValue(tenant.Id, out var due) ? due : null, today);
            if (tenant.ApplyBillingStage(stage)) changes.Add(new StandingChange(tenant.Id, tenant.Slug, from, tenant.Status));
        }
        return changes;
    }

    /// <summary>
    /// Depois de salvar: a vitrine e o painel deste processo veem a situação nova na hora (os outros em até 60 s, pelo
    /// cache do catálogo) e, se a loja parou ou voltou a vender nos canais, os anúncios são reconferidos.
    /// </summary>
    public static async Task AnnounceAsync(IReadOnlyList<StandingChange> changes, ITenantCatalog catalog, IMemoryCache cache, IMessageBus bus,
        ILogger logger)
    {
        foreach (var change in changes)
        {
            catalog.Invalidate(change.TenantId);
            cache.Remove(StoreProfileService.PublicKey(change.TenantId));
            LogChanged(logger, change.Slug, change.TenantId, change.From, change.To);
            await PublishChannelResyncAsync(bus, change.TenantId, change.From, change.To);
        }
    }

    /// <summary>Suspensão ou reativação (pela régua ou pela equipe): pausa ou retoma os anúncios nos canais.</summary>
    public static async Task PublishChannelResyncAsync(IMessageBus bus, Guid tenantId, TenantStatus from, TenantStatus to)
    {
        if (Tenant.IsSellingOnChannels(from) != Tenant.IsSellingOnChannels(to))
            await bus.PublishAsync(new ResyncStoreListings(), new DeliveryOptions { TenantId = tenantId.ToString() });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Régua de inadimplência: loja {Slug} ({TenantId}) passou de {From} para {To}")]
    private static partial void LogChanged(ILogger logger, string slug, Guid tenantId, TenantStatus from, TenantStatus to);
}
