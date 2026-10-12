using Ecommerce.Application.Email;
using Ecommerce.Application.Integrations;
using System.Globalization;
using Ecommerce.Application.Inventory;
using Ecommerce.Application.Payments;
using Ecommerce.Domain.Inventory;
using Ecommerce.Domain.Orders;
using Ecommerce.Domain.Payments;
using Ecommerce.Infrastructure.Orders;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Ecommerce.Infrastructure.Payments;

/// <summary>
/// Pagamento pela fila (RF15). O pedido só vira pago depois de consultar o gateway (CA3); se o gateway falhar, a
/// exceção faz o Wolverine tentar de novo. Tudo — pagamento, pedido, baixa e eventos — numa transação.
/// </summary>
public static class PaymentHandler
{
    public const string LateSaleReason = "Pagamento aprovado depois que uma das peças foi vendida. Devolva o valor ao comprador ou combine outra peça.";
    public const string RefundedReason = "O pagamento foi estornado no gateway depois de aprovado. Confira o pedido e o estoque.";
    public const string CanceledByStoreReason = "Pagamento aprovado de um pedido que a loja já tinha cancelado. Devolva o valor ao comprador ou aprove a venda.";

    public static async Task<(RecordOutcome, OutgoingMessages)> Handle(RecordPaymentNotification command, TenantDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        var id = Guid.CreateVersion7();
        var inserted = await db.Database.SqlQuery<Guid>($"""
            INSERT INTO external_events (id, tenant_id, source, idempotency_key, resource_id, status, received_at)
            VALUES ({id}, {db.RequireTenantId()}, {command.Gateway}, {command.IdempotencyKey}, {command.PaymentId},
                    {nameof(ExternalEventStatus.Received)}, {clock.GetUtcNow()})
            ON CONFLICT (tenant_id, source, idempotency_key) DO NOTHING
            RETURNING id AS "Value"
            """).ToListAsync(ct);
        if (inserted.Count == 0) return (RecordOutcome.Duplicate, outgoing);

        outgoing.Add(new ProcessPaymentNotification(id));
        return (RecordOutcome.Recorded, outgoing);
    }

    public static async Task<OutgoingMessages> Handle(ProcessPaymentNotification command, TenantDbContext db, IPaymentGatewayResolver gateways,
        IIntegrationLog integrationLog, TimeProvider clock, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        var notification = await db.ExternalEvents.SingleOrDefaultAsync(e => e.Id == command.EventId, ct);
        if (notification is not { Status: ExternalEventStatus.Received }) return outgoing;
        var now = clock.GetUtcNow();

        var gateway = await gateways.ResolveAsync(notification.Source, ct);
        var info = gateway is null ? null : await gateway.GetPaymentAsync(notification.ResourceId, ct);
        var orderId = info is null ? null
            : Guid.TryParse(info.OrderReference, out var id)
                ? await db.CustomerOrders.Where(o => o.Id == id).Select(o => (Guid?)o.Id).SingleOrDefaultAsync(ct)
                : OrderReference.NumberOf(info.OrderReference) is { } number
                    ? await db.CustomerOrders.Where(o => o.Number == number).Select(o => (Guid?)o.Id).SingleOrDefaultAsync(ct)
                    : null;
        if (gateway is null || info is null || orderId is null)
        {
            notification.Finish(ExternalEventStatus.Ignored, now);
            return outgoing;
        }

        // A consulta ao gateway foi registrada antes de se saber o pedido: liga ao pedido no log de integração (RF29).
        await integrationLog.LinkOrderAsync(orderId.Value, ct);

        // Pedido travado: ações do painel e o prazo sobre ele esperam (e vice-versa) — ver OrderStock.LockAsync.
        await OrderStock.LockAsync(db, orderId.Value, ct);
        var order = await db.CustomerOrders.Include(o => o.Items).SingleAsync(o => o.Id == orderId, ct);
        var from = order.Status;

        var payment = await db.Payments.SingleOrDefaultAsync(p => p.Gateway == gateway.Name && p.ExternalId == info.ExternalId, ct);
        if (payment is null) db.Payments.Add(new Payment(order.TenantId, order.Id, gateway.Name, info.ExternalId, info.Status, info.Amount, info.Method, now));
        else payment.Update(info.Status, info.Amount, info.Method, now);

        if (info.Status == PaymentStatus.Approved && order.AcceptsPayment)
            outgoing.AddRange(await ConfirmAsync(db, order, info.Amount, now, ct));
        else if (info.Status == PaymentStatus.Approved && order is { Status: OrderStatus.Canceled, CanceledBy: CancellationSource.Store })
            order.FlagForReview(CanceledByStoreReason);
        else if (info.Status == PaymentStatus.Refunded && order.Status is OrderStatus.Paid or OrderStatus.Shipped or OrderStatus.Delivered or OrderStatus.PickedUp)
            order.FlagForReview(RefundedReason);

        if (order.Status != from)
            db.OrderHistory.Add(new OrderHistoryEntry(order.TenantId, order.Id, from, order.Status, OrderActor.Payment, null,
                order.Status == OrderStatus.NeedsReview ? order.ReviewReason : $"Pagamento aprovado ({gateway.Name}).", now));
        notification.Finish(ExternalEventStatus.Processed, now);
        return outgoing;
    }

    /// <summary>
    /// Baixa todas as peças do pedido ou nenhuma. Reserva expirada ainda vale se houver saldo (RF12 CA3); se alguma peça já
    /// foi vendida, tudo é desfeito até o savepoint e o pedido fica para a loja resolver — sem estorno automático (HIPÓTESE).
    /// </summary>
    private static async Task<OutgoingMessages> ConfirmAsync(TenantDbContext db, CustomerOrder order, decimal amount, DateTimeOffset now, CancellationToken ct)
    {
        if (amount != order.Total)
        {
            order.FlagForReview(string.Create(CultureInfo.InvariantCulture,
                $"Valor pago (R$ {amount:0.00}) diferente do total do pedido (R$ {order.Total:0.00}). Confira com o comprador."));
            return [];
        }

        var (missing, events) = await OrderStock.ConfirmAllAsync(db, order, now, ct);
        if (missing is not null)
        {
            order.FlagForReview(LateSaleReason);
            return [];
        }
        order.MarkPaid(now);
        events.Add(new NotifyBuyer(order.Id, OrderEmail.Paid));
        return events;
    }
}
