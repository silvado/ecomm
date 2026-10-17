using Ecommerce.Application.Channels;
using Ecommerce.Application.Inventory;
using Ecommerce.Domain.Inventory;
using Ecommerce.Domain.Orders;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Ecommerce.Infrastructure.Orders;

/// <summary>
/// Vendas do Mercado Livre no painel (RF21, RF17 CA2, RNF02, Q01), na transação do Wolverine. Pago no ML e ainda não
/// importado: cria o pedido unificado e baixa cada peça por UPDATE condicional (movimento "venda direta"). Peça sem saldo
/// — vendida em outro canal dentro da janela de propagação — não é baixada: o pedido entra em análise com "conflito de
/// estoque" e a loja decide (nenhum cancelamento automático). Cancelado no ML: cancela aqui e devolve as peças baixadas.
/// O índice único (loja, origem, id externo) barra a importação em dobro; a corrida perdida tenta de novo e vê o pedido.
/// </summary>
public static class ChannelOrderHandler
{
    public static readonly IReadOnlySet<string> CanceledStatuses = new HashSet<string>(StringComparer.Ordinal) { "cancelled", "invalid" };

    private sealed record Line(ChannelOrderItem Item, Guid PartId, string InternalCode);

    public static async Task<(ChannelOrderResult, OutgoingMessages)> Handle(ImportChannelOrder command, TenantDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        var snapshot = command.Order;
        var tenantId = db.RequireTenantId();
        var now = clock.GetUtcNow();

        var existing = await db.CustomerOrders.Include(o => o.Items)
            .SingleOrDefaultAsync(o => o.Origin == OrderOrigin.MercadoLivre && o.ExternalId == snapshot.ExternalId, ct);
        if (existing is not null)
        {
            if (!CanceledStatuses.Contains(snapshot.Status) || existing.Status == OrderStatus.Canceled || !existing.CanBeCanceledByStore)
                return (new ChannelOrderResult(ChannelOrderOutcome.Unchanged, existing.Id, existing.Number), outgoing);
            await OrderStock.LockAsync(db, existing.Id, ct);
            var previous = existing.Status;
            outgoing.AddRange(await OrderStock.ReturnAllAsync(db, existing, null, now, ct));
            existing.CancelByStore("Cancelado no Mercado Livre.", now);
            db.OrderHistory.Add(new OrderHistoryEntry(tenantId, existing.Id, previous, existing.Status, OrderActor.System, null,
                "Cancelado no Mercado Livre: as peças voltaram ao estoque.", now));
            return (new ChannelOrderResult(ChannelOrderOutcome.Canceled, existing.Id, existing.Number), outgoing);
        }
        if (snapshot.Status != "paid") return (new ChannelOrderResult(ChannelOrderOutcome.Ignored), outgoing);

        // Anúncio do ML → peça da loja (só anúncios publicados pela plataforma nesta conexão).
        var itemIds = snapshot.Items.Select(i => i.ExternalItemId).Distinct().ToList();
        var parts = await (from l in db.ChannelListings.AsNoTracking()
                           join p in db.Parts.AsNoTracking() on l.PartId equals p.Id
                           where l.ConnectionId == command.ConnectionId && l.ExternalId != null && itemIds.Contains(l.ExternalId)
                           select new { ExternalId = l.ExternalId!, PartId = p.Id, p.InternalCode }).ToListAsync(ct);
        var lines = snapshot.Items
            .GroupBy(i => i.ExternalItemId)
            .Select(g => (Item: g.First() with { Quantity = g.Sum(i => i.Quantity) }, Part: parts.FirstOrDefault(p => p.ExternalId == g.Key)))
            .ToList();
        var known = lines.Where(l => l.Part is not null).Select(l => new Line(l.Item, l.Part!.PartId, l.Part.InternalCode))
            .GroupBy(l => l.PartId).Select(g => g.First() with { Item = g.First().Item with { Quantity = g.Sum(x => x.Item.Quantity) } })
            .Take(CustomerOrder.MaxLines).ToList();
        var unknown = lines.Where(l => l.Part is null).Select(l => l.Item).ToList();
        if (known.Count == 0) return (new ChannelOrderResult(ChannelOrderOutcome.Ignored), outgoing);

        var number = (await db.Database.SqlQuery<long>($"""
            INSERT INTO order_counters (tenant_id, last_number) VALUES ({tenantId}, 1)
            ON CONFLICT (tenant_id) DO UPDATE SET last_number = order_counters.last_number + 1
            RETURNING last_number AS "Value"
            """).ToListAsync(ct)).Single();
        var reference = $"pedido {number}";

        var orderLines = new List<OrderLine>();
        var conflicts = new List<string>();
        // Ordem fixa por peça: vendas concorrentes travam as linhas na mesma ordem.
        foreach (var line in known.OrderBy(l => l.PartId))
        {
            var available = (await db.Database.SqlQuery<int>($"""
                UPDATE stocks SET on_hand = on_hand - {line.Item.Quantity}
                WHERE part_id = {line.PartId} AND on_hand - reserved >= {line.Item.Quantity}
                RETURNING on_hand - reserved AS "Value"
                """).ToListAsync(ct)).Cast<int?>().SingleOrDefault();
            StockReservation reservation;
            if (available is { } left)
            {
                reservation = StockReservation.Sold(tenantId, line.PartId, line.Item.Quantity, reference, now);
                db.StockMovements.Add(new StockMovement(tenantId, line.PartId, -line.Item.Quantity, StockMovementReason.DirectSale, reference, reservation.Id, now, null));
                outgoing.Add(new StockChanged(line.PartId, left));
            }
            else
            {
                reservation = StockReservation.Unfulfilled(tenantId, line.PartId, line.Item.Quantity, reference, now);
                conflicts.Add(line.InternalCode);
            }
            db.StockReservations.Add(reservation);
            var price = decimal.Round(line.Item.UnitPrice, 2) > 0 ? decimal.Round(line.Item.UnitPrice, 2) : 0.01m;
            orderLines.Add(new OrderLine(line.PartId, line.InternalCode, Cut(line.Item.Title, Domain.Catalog.Part.TitleMaxLength), price, line.Item.Quantity, reservation.Id));
        }

        var reasons = new List<string>();
        if (conflicts.Count > 0)
            reasons.Add($"Conflito de estoque: {string.Join(", ", conflicts)} sem saldo (vendida em outro canal). Reponha ou cancele a venda no Mercado Livre.");
        if (unknown.Count > 0)
            reasons.Add($"Itens que não são anúncios da plataforma: {string.Join(", ", unknown.Select(u => u.ExternalItemId))}.");
        var reason = reasons.Count == 0 ? null : Cut(string.Join(" ", reasons), 300);

        var order = CustomerOrder.PlaceChannelOrder(tenantId, number, OrderOrigin.MercadoLivre, snapshot.ExternalId, snapshot.BuyerName ?? string.Empty,
            orderLines, Math.Max(0, decimal.Round(snapshot.ShippingCost, 2)), reason, snapshot.PaidAt ?? now, now);
        order.LinkShipment(snapshot.ShipmentId);
        db.CustomerOrders.Add(order);
        // O envio pode ter andado antes de a venda chegar aqui (aviso de envio sem pedido): confere logo em seguida.
        if (order.ExternalShipmentId is { } shipmentId) outgoing.Add(new SyncChannelShipment(command.ConnectionId, shipmentId));
        db.OrderHistory.Add(new OrderHistoryEntry(tenantId, order.Id, null, order.Status, OrderActor.System, null,
            $"Venda no Mercado Livre (pedido {snapshot.ExternalId}){(reason is null ? string.Empty : " · " + reason)}", now));
        return (new ChannelOrderResult(reason is null ? ChannelOrderOutcome.Created : ChannelOrderOutcome.CreatedForReview, order.Id, order.Number), outgoing);
    }

    private static string Cut(string text, int max) => text.Length > max ? text[..max] : text;
}
