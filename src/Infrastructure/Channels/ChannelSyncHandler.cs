using Ecommerce.Application.Channels;
using Ecommerce.Application.Integrations;
using Ecommerce.Application.Inventory;
using Ecommerce.Application.Tenancy;
using Ecommerce.Domain.Channels;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace Ecommerce.Infrastructure.Channels;

/// <summary>
/// Propagação de estoque para os canais (RF24, RF25 CA2). Cada mudança de estoque vira uma mensagem por anúncio; cada
/// uma lê o saldo atual, decide (pausar ao zerar, reativar o que o sistema pausou, atualizar quantidade) e chama o canal.
/// A linha do anúncio fica travada durante a sincronização: duas mudanças seguidas não se atropelam no canal.
/// </summary>
public static partial class ChannelSyncHandler
{
    public static async Task<OutgoingMessages> Handle(StockChanged @event, Envelope envelope, TenantDbContext db, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        var listings = await (from l in db.ChannelListings
                              join c in db.ChannelConnections on l.ConnectionId equals c.Id
                              where l.PartId == @event.PartId && c.Status == ChannelConnectionStatus.Active
                                    && (l.Status == ListingStatus.Active || l.Status == ListingStatus.PausedBySystem || l.Status == ListingStatus.Error)
                              select l.Id).ToListAsync(ct);
        foreach (var id in listings) outgoing.Add(new SyncListing(id, envelope.SentAt));
        // Catálogo da Meta (RF22 CA2): o item segue o estoque sem anúncio por peça (o feed já tem todas).
        if (await db.ChannelConnections.AnyAsync(c => c.Channel == SalesChannel.Meta && c.Status == ChannelConnectionStatus.Active, ct))
            outgoing.Add(new SyncMetaCatalogItem(@event.PartId));
        return outgoing;
    }

    public static async Task Handle(SyncListing command, TenantDbContext db, IChannelConnectorResolver connectors, IIntegrationLog log,
        ITenantCatalog catalog, ChannelSyncMetrics metrics, TimeProvider clock, ILogger<SyncListing> logger, CancellationToken ct)
    {
        if (await LockAsync(db, command.ListingId, ct) is not { } listing) return;
        var connection = await db.ChannelConnections.SingleAsync(c => c.Id == listing.ConnectionId, ct);
        if (connection.Status != ChannelConnectionStatus.Active || listing.ExternalId is not { } externalId) return;
        var (available, price) = await StockAndPriceAsync(db, catalog, listing.PartId, ct);

        var action = listing.Decide(available, price);
        if (action == ListingAction.None) return;
        if (await connectors.ResolveAsync(connection, ct) is not { } resolved)
        {
            listing.Failed("Canal sem conector disponível.", clock.GetUtcNow());
            return;
        }
        var connector = new LoggedChannelConnector(resolved, log, listing.PartId);

        // Falha de comunicação sobe: a política da fila tenta de novo (RF28) e, esgotadas, registra a falha.
        switch (action)
        {
            case ListingAction.Pause:
                await connector.PauseAsync(externalId, ct);
                break;
            case ListingAction.Resume:
                await connector.ResumeAsync(externalId, available, ct);
                if (price != listing.LastPushedPrice) await connector.UpdatePriceAndQuantityAsync(externalId, price, available, ct);
                break;
            case ListingAction.UpdateQuantity:
                if (listing.Status == ListingStatus.Error && available > 0) await connector.ResumeAsync(externalId, available, ct);
                await connector.UpdatePriceAndQuantityAsync(externalId, price, available, ct);
                break;
        }
        var now = clock.GetUtcNow();
        listing.Applied(action, available, price, now);

        if (command.ChangedAt is { } changedAt)
        {
            var latency = now - changedAt;
            metrics.Record(connection.Channel, latency);
            if (latency > ChannelSyncMetrics.Target) LogLate(logger, connection.Channel, listing.Id, latency.TotalSeconds);
        }
    }

    public static async Task<ListingCommandResult> Handle(ChangeListingByUser command, TenantDbContext db, IChannelConnectorResolver connectors,
        IIntegrationLog log, ITenantCatalog catalog, TimeProvider clock, CancellationToken ct)
    {
        if (await LockAsync(db, command.ListingId, ct) is not { } listing) return new(ListingCommandOutcome.NotFound, "Anúncio não encontrado.");
        var connection = await db.ChannelConnections.SingleAsync(c => c.Id == listing.ConnectionId, ct);
        if (listing.ExternalId is not { } externalId || connection.Status != ChannelConnectionStatus.Active
            || await connectors.ResolveAsync(connection, ct) is not { } resolved)
            return new(ListingCommandOutcome.NotAllowed, "O canal deste anúncio não está conectado.");
        var connector = new LoggedChannelConnector(resolved, log, listing.PartId);
        var (available, price) = await StockAndPriceAsync(db, catalog, listing.PartId, ct);
        var now = clock.GetUtcNow();

        try
        {
            if (command.Pause)
            {
                if (listing.Status is not (ListingStatus.Active or ListingStatus.PausedBySystem or ListingStatus.Error))
                    return new(ListingCommandOutcome.NotAllowed, "Só anúncio ativo ou pausado pelo sistema pode ser pausado.");
                if (listing.Status != ListingStatus.PausedBySystem) await connector.PauseAsync(externalId, ct);
                listing.PauseByUser(now);
            }
            else
            {
                if (listing.Status != ListingStatus.PausedByUser) return new(ListingCommandOutcome.NotAllowed, "O anúncio não está pausado pelo lojista.");
                if (available > 0)
                {
                    await connector.ResumeAsync(externalId, available, ct);
                    await connector.UpdatePriceAndQuantityAsync(externalId, price, available, ct);
                }
                listing.ResumeByUser(available, price, now);
            }
        }
        catch (ChannelConnectorException e)
        {
            return new(ListingCommandOutcome.NotAllowed, $"O canal não respondeu: {e.Message} Tente de novo em instantes.");
        }
        return new(ListingCommandOutcome.Done);
    }

    /// <summary>Trava a linha do anúncio até o fim da transação da mensagem.</summary>
    internal static async Task<ChannelListing?> LockAsync(TenantDbContext db, Guid listingId, CancellationToken ct)
    {
        var locked = await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM channel_listings WHERE id = {listingId} FOR UPDATE").ToListAsync(ct);
        return locked.Count == 0 ? null : await db.ChannelListings.SingleAsync(l => l.Id == listingId, ct);
    }

    internal static async Task<(int Available, decimal Price)> StockAndPriceAsync(TenantDbContext db, ITenantCatalog catalog, Guid partId, CancellationToken ct)
    {
        var row = await (from p in db.Parts
                         join s in db.Stocks on p.Id equals s.PartId
                         where p.Id == partId
                         select new { p.Price, p.Status, Available = s.OnHand - s.Reserved }).SingleAsync(ct);
        // Loja suspensa ou em encerramento (RF38; decisão do usuário, 16/10/2026) conta como sem saldo: os anúncios pausam
        // e voltam sozinhos quando ela é reativada. Assim nenhum canal vende sem o estoque chegar aos outros.
        var store = await catalog.FindByIdAsync(db.RequireTenantId(), ct);
        if (store is not null && !Tenant.IsSellingOnChannels(store.Status)) return (0, row.Price);
        // Peça inativa ou em rascunho conta como sem saldo: o anúncio pausa.
        return (row.Status == Domain.Catalog.PartStatus.Active ? Math.Max(0, row.Available) : 0, row.Price);
    }

    /// <summary>
    /// A loja passou a vender ou deixou de vender nos canais (suspensão ou reativação — RF38): todos os anúncios que seguem o
    /// estoque são conferidos de novo. Lê a situação fresca (o catálogo deste processo pode estar até 60 s atrasado).
    /// </summary>
    public static async Task<OutgoingMessages> Handle(ResyncStoreListings command, TenantDbContext db, ITenantCatalog catalog, CancellationToken ct)
    {
        catalog.Invalidate(db.RequireTenantId());
        var outgoing = new OutgoingMessages();
        var listings = await (from l in db.ChannelListings
                              join c in db.ChannelConnections on l.ConnectionId equals c.Id
                              where c.Status == ChannelConnectionStatus.Active
                                    && (l.Status == ListingStatus.Active || l.Status == ListingStatus.PausedBySystem || l.Status == ListingStatus.Error)
                              select l.Id).ToListAsync(ct);
        foreach (var id in listings) outgoing.Add(new SyncListing(id, null));
        return outgoing;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Propagação de estoque acima de 60 s no canal {Channel} (anúncio {ListingId}): {Seconds:0.0} s")]
    private static partial void LogLate(ILogger logger, string channel, Guid listingId, double seconds);
}
