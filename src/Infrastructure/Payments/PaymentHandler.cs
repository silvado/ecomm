using System.Globalization;
using Ecommerce.Application.Inventory;
using Ecommerce.Application.Payments;
using Ecommerce.Domain.Inventory;
using Ecommerce.Domain.Orders;
using Ecommerce.Domain.Payments;
using Ecommerce.Infrastructure.Inventory;
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
    private const string Savepoint = "confirmacao_pagamento";

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
        TimeProvider clock, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        var notification = await db.ExternalEvents.SingleOrDefaultAsync(e => e.Id == command.EventId, ct);
        if (notification is not { Status: ExternalEventStatus.Received }) return outgoing;
        var now = clock.GetUtcNow();

        var gateway = await gateways.ResolveAsync(notification.Source, ct);
        var info = gateway is null ? null : await gateway.GetPaymentAsync(notification.ResourceId, ct);
        var order = info is null ? null
            : Guid.TryParse(info.OrderReference, out var orderId)
                ? await db.CustomerOrders.Include(o => o.Items).SingleOrDefaultAsync(o => o.Id == orderId, ct)
                : OrderReference.NumberOf(info.OrderReference) is { } number
                    ? await db.CustomerOrders.Include(o => o.Items).SingleOrDefaultAsync(o => o.Number == number, ct)
                    : null;
        if (gateway is null || info is null || order is null)
        {
            notification.Finish(ExternalEventStatus.Ignored, now);
            return outgoing;
        }

        var payment = await db.Payments.SingleOrDefaultAsync(p => p.Gateway == gateway.Name && p.ExternalId == info.ExternalId, ct);
        if (payment is null) db.Payments.Add(new Payment(order.TenantId, order.Id, gateway.Name, info.ExternalId, info.Status, info.Amount, info.Method, now));
        else payment.Update(info.Status, info.Amount, info.Method, now);

        if (info.Status == PaymentStatus.Approved && order.Status is OrderStatus.PendingPayment or OrderStatus.Canceled)
            outgoing.AddRange(await ConfirmAsync(db, order, info.Amount, now, ct));
        else if (info.Status == PaymentStatus.Refunded && order.Status == OrderStatus.Paid)
            order.FlagForReview(RefundedReason);

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

        var transaction = db.Database.CurrentTransaction ?? throw new InvalidOperationException("Confirmação de pagamento fora de transação.");
        await transaction.CreateSavepointAsync(Savepoint, ct);
        var outgoing = new OutgoingMessages();
        foreach (var item in order.Items.OrderBy(i => i.PartId))
        {
            var (outcome, events) = await StockHandler.ConfirmAsync(db, item.ReservationId, now, ct);
            if (outcome is ConfirmReservationOutcome.OutOfStock or ConfirmReservationOutcome.NotFound)
            {
                await transaction.RollbackToSavepointAsync(Savepoint, ct);
                foreach (var movement in db.ChangeTracker.Entries<StockMovement>().Where(e => e.State == EntityState.Added).ToList())
                    movement.State = EntityState.Detached;
                order.FlagForReview(LateSaleReason);
                return [];
            }
            outgoing.AddRange(events);
        }
        order.MarkPaid(now);
        return outgoing;
    }
}
