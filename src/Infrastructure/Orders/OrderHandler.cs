using Ecommerce.Application.Inventory;
using Ecommerce.Application.Orders;
using Ecommerce.Domain.Inventory;
using Ecommerce.Domain.Orders;
using Ecommerce.Infrastructure.Inventory;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Ecommerce.Infrastructure.Orders;

/// <summary>
/// Pedido da loja (RF14) com reserva atômica (RF12, RNF02). Roda na transação do Wolverine (AutoApplyTransactions):
/// reservas, número, pedido, eventos <see cref="StockChanged"/> e a expiração agendada são confirmados juntos.
/// O mesmo token duas vezes esbarra no índice único (<see cref="AccessTokenIndex"/>) e nada é gravado.
/// </summary>
public static class OrderHandler
{
    public const string AccessTokenIndex = "ix_customer_orders_tenant_id_access_token_hash";
    public const string ExpiredReason = "Pagamento não confirmado no prazo.";


    public static async Task<(PlaceOrderResult, OutgoingMessages)> Handle(PlaceOrder command, TenantDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        var tenantId = db.RequireTenantId();
        var now = clock.GetUtcNow();
        var minutes = await db.StoreSettings.Select(s => (int?)s.ReservationMinutes).SingleOrDefaultAsync(ct)
            ?? StoreSettings.DefaultReservationMinutes;
        var deadline = now.AddMinutes(minutes);

        // Todas as peças ou nenhuma. Cada reserva é o UPDATE condicional do RNF02; na falta de saldo, as anteriores são
        // desfeitas na mesma transação (as linhas seguem travadas por ela, ninguém viu o saldo intermediário).
        // Ordem fixa por peça: pedidos concorrentes com as mesmas peças travam as linhas na mesma ordem (sem deadlock).
        var lines = command.Lines.OrderBy(l => l.PartId).ToList();
        var reserved = new List<(PlaceOrderLine Line, int Available)>();
        foreach (var line in lines)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(line.Quantity);
            var available = (await db.Database.SqlQuery<int>($"""
                UPDATE stocks SET reserved = reserved + {line.Quantity}
                WHERE part_id = {line.PartId} AND on_hand - reserved >= {line.Quantity}
                RETURNING on_hand - reserved AS "Value"
                """).ToListAsync(ct)).Cast<int?>().SingleOrDefault();
            if (available is null)
            {
                foreach (var (done, _) in reserved)
                    await db.Database.ExecuteSqlAsync($"UPDATE stocks SET reserved = reserved - {done.Quantity} WHERE part_id = {done.PartId}", ct);
                return (PlaceOrderResult.Unavailable(line.PartId), outgoing);
            }
            reserved.Add((line, available.Value));
        }

        // Número sequencial da loja: a linha do contador fica travada até o fim da transação.
        var number = (await db.Database.SqlQuery<long>($"""
            INSERT INTO order_counters (tenant_id, last_number) VALUES ({tenantId}, 1)
            ON CONFLICT (tenant_id) DO UPDATE SET last_number = order_counters.last_number + 1
            RETURNING last_number AS "Value"
            """).ToListAsync(ct)).Single();

        var orderLines = new List<OrderLine>();
        foreach (var (line, available) in reserved)
        {
            var reservation = new StockReservation(tenantId, line.PartId, line.Quantity, $"pedido {number}", now, deadline);
            db.StockReservations.Add(reservation);
            orderLines.Add(new OrderLine(line.PartId, line.InternalCode, line.Title, line.UnitPrice, line.Quantity, reservation.Id));
            outgoing.Add(new StockChanged(line.PartId, available));
        }

        var order = CustomerOrder.Place(tenantId, number, command.AccessTokenHash, command.Buyer, command.Address, command.Shipping,
            command.PickupAddress, orderLines, now, deadline);
        db.CustomerOrders.Add(order);
        outgoing.Schedule(new ExpireOrder(order.Id), deadline);
        return (new PlaceOrderResult(PlaceOrderOutcome.Placed, order.Number, order.Total, order.PaymentDeadline, null), outgoing);
    }

    /// <summary>Fim do prazo sem pagamento: cancela o pedido e devolve as peças. Repetir (ou chegar depois do pagamento) não tem efeito.</summary>
    public static async Task<OutgoingMessages> Handle(ExpireOrder command, TenantDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        var now = clock.GetUtcNow();

        var canceled = await db.Database.SqlQuery<Guid>($"""
            UPDATE customer_orders SET status = {nameof(OrderStatus.Canceled)}, canceled_at = {now}, cancel_reason = {ExpiredReason}
            WHERE id = {command.OrderId} AND status = {nameof(OrderStatus.PendingPayment)} AND payment_deadline <= {now}
            RETURNING id AS "Value"
            """).ToListAsync(ct);
        if (canceled.Count == 0)
        {
            // Disparo adiantado (relógios diferentes): agenda de novo para o prazo.
            var pending = await db.CustomerOrders.AsNoTracking()
                .Where(o => o.Id == command.OrderId && o.Status == OrderStatus.PendingPayment)
                .Select(o => (DateTimeOffset?)o.PaymentDeadline).SingleOrDefaultAsync(ct);
            if (pending is { } deadline) outgoing.Schedule(command, deadline);
            return outgoing;
        }

        var reservations = await db.OrderItems.Where(i => i.OrderId == command.OrderId).Select(i => i.ReservationId).ToListAsync(ct);
        foreach (var reservation in reservations)
            outgoing.AddRange(await StockHandler.EndReservationAsync(db, reservation, ReservationStatus.Expired, expiredBefore: now, ct));
        return outgoing;
    }


}
