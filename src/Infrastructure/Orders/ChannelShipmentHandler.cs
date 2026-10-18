using Ecommerce.Application.Channels;
using Ecommerce.Domain.Orders;
using Ecommerce.Infrastructure.Channels.MercadoLivre;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Orders;

/// <summary>
/// Envio das vendas do ML no painel (RF21, RF17), na transação do Wolverine: consulta o envio no ML (o aviso nunca é
/// confiado) e leva a situação ao pedido — <c>shipped</c> marca enviado com o rastreio, <c>delivered</c> marca entregue
/// (passando por enviado se o aviso de envio se perdeu). <c>not_delivered</c> e <c>cancelled</c> só vão para o histórico:
/// a loja resolve com o ML (reclamação, devolução). Repetir não muda nada.
/// </summary>
public static class ChannelShipmentHandler
{
    public static async Task<ShipmentOutcome> Handle(SyncChannelShipment command, TenantDbContext db, MercadoLivreApiClient api, MercadoLivreCredentials credentials,
        TimeProvider clock, CancellationToken ct)
    {
        var order = await db.CustomerOrders.SingleOrDefaultAsync(o => o.Origin == OrderOrigin.MercadoLivre && o.ExternalShipmentId == command.ShipmentId, ct);
        if (order is null) return ShipmentOutcome.NoOrder;

        var token = await credentials.AccessTokenAsync(command.ConnectionId, ct);
        if (await api.GetShipmentAsync(token, command.ShipmentId, ct) is not { } shipment) return ShipmentOutcome.Unchanged;

        await OrderStock.LockAsync(db, order.Id, ct);
        await db.Entry(order).ReloadAsync(ct);
        var now = clock.GetUtcNow();
        var tenantId = db.RequireTenantId();
        var tracking = shipment.TrackingNumber is { Length: > 0 and <= CustomerOrder.TrackingCodeMaxLength } t ? t : null;

        void History(OrderStatus from, string note) =>
            db.OrderHistory.Add(new OrderHistoryEntry(tenantId, order.Id, from, order.Status, Domain.Orders.OrderActor.System, null, note, now));

        switch (shipment.Status)
        {
            case "shipped" when order.Status == OrderStatus.Paid:
                order.MarkShipped(tracking, now);
                History(OrderStatus.Paid, tracking is null ? "Enviado pelo Mercado Livre." : $"Enviado pelo Mercado Livre · rastreio {tracking}");
                return ShipmentOutcome.Shipped;
            case "delivered" when order.Status is OrderStatus.Paid or OrderStatus.Shipped:
                if (order.Status == OrderStatus.Paid)
                {
                    order.MarkShipped(tracking, now);
                    History(OrderStatus.Paid, "Enviado pelo Mercado Livre.");
                }
                order.MarkDelivered(now);
                History(OrderStatus.Shipped, "Entregue ao comprador, segundo o Mercado Livre.");
                return ShipmentOutcome.Delivered;
            case "not_delivered" or "cancelled" when order.Status is OrderStatus.Paid or OrderStatus.Shipped or OrderStatus.NeedsReview:
                var note = shipment.Status == "cancelled"
                    ? $"Envio cancelado no Mercado Livre{(shipment.Substatus is null ? "" : $" ({shipment.Substatus})")}. Confira a venda em Minhas vendas."
                    : $"Não entregue, segundo o Mercado Livre{(shipment.Substatus is null ? "" : $" ({shipment.Substatus})")}. Confira a venda em Minhas vendas.";
                // Uma anotação por situação do envio (o ML avisa várias vezes).
                if (await db.OrderHistory.AnyAsync(h => h.OrderId == order.Id && h.Note == note, ct)) return ShipmentOutcome.Unchanged;
                History(order.Status, note);
                return ShipmentOutcome.Noted;
            default:
                return ShipmentOutcome.Unchanged;
        }
    }
}
