using Ecommerce.Application.Inventory;
using Ecommerce.Domain.Inventory;
using Ecommerce.Domain.Orders;
using Ecommerce.Infrastructure.Inventory;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Ecommerce.Infrastructure.Orders;

/// <summary>
/// Estoque de um pedido inteiro (RF12, RF15, RF17, RNF02). Sempre na transação do handler e com o pedido travado
/// (<see cref="LockAsync"/>): cada peça é um UPDATE condicional, então repetir não baixa nem devolve duas vezes.
/// </summary>
internal static class OrderStock
{
    private const string Savepoint = "estoque_do_pedido";

    private sealed record ReservationRow(Guid PartId, int Quantity);

    /// <summary>
    /// Trava a linha do pedido até o fim da transação: pagamento, prazo e ações do painel sobre o mesmo pedido são
    /// aplicados um de cada vez, sempre sobre a situação mais recente.
    /// </summary>
    public static async Task LockAsync(TenantDbContext db, Guid orderId, CancellationToken ct) =>
        await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM customer_orders WHERE id = {orderId} FOR UPDATE").ToListAsync(ct);

    /// <summary>
    /// Baixa todas as peças do pedido ou nenhuma: reserva ativa vira baixa, reserva vencida ou liberada vira baixa direta
    /// se houver saldo, peça já baixada fica como está. Faltando saldo, desfaz tudo até o savepoint e devolve a peça.
    /// </summary>
    public static async Task<(OrderItem? Missing, OutgoingMessages Events)> ConfirmAllAsync(TenantDbContext db, CustomerOrder order, DateTimeOffset now, CancellationToken ct)
    {
        var transaction = db.Database.CurrentTransaction ?? throw new InvalidOperationException("Baixa do pedido fora de transação.");
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
                return (item, []);
            }
            outgoing.AddRange(events);
        }
        return (null, outgoing);
    }

    /// <summary>Cancelamento: reserva ativa é liberada; peça já baixada volta ao estoque (entrada com movimento).</summary>
    public static async Task<OutgoingMessages> ReturnAllAsync(TenantDbContext db, CustomerOrder order, Guid? userId, DateTimeOffset now, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        foreach (var item in order.Items.OrderBy(i => i.PartId))
        {
            outgoing.AddRange(await StockHandler.EndReservationAsync(db, item.ReservationId, ReservationStatus.Released, expiredBefore: null, ct));

            var returned = (await db.Database.SqlQuery<ReservationRow>($"""
                UPDATE stock_reservations SET status = {nameof(ReservationStatus.Returned)}
                WHERE id = {item.ReservationId} AND status = {nameof(ReservationStatus.Converted)}
                RETURNING part_id, quantity
                """).ToListAsync(ct)).SingleOrDefault();
            if (returned is null) continue;

            var available = (await db.Database.SqlQuery<int>($"""
                UPDATE stocks SET on_hand = on_hand + {returned.Quantity}
                WHERE part_id = {returned.PartId}
                RETURNING on_hand - reserved AS "Value"
                """).ToListAsync(ct)).Single();
            db.StockMovements.Add(new StockMovement(db.RequireTenantId(), returned.PartId, returned.Quantity, StockMovementReason.OrderCanceled,
                $"pedido {order.Number}", item.ReservationId, now, userId));
            outgoing.Add(new StockChanged(returned.PartId, available));
        }
        return outgoing;
    }
}
