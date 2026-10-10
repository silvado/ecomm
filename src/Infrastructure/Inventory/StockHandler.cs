using Ecommerce.Application.Inventory;
using Ecommerce.Domain.Inventory;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Ecommerce.Infrastructure.Inventory;

/// <summary>
/// Operações de estoque (RF12, RNF02). Cada mudança de saldo é um único UPDATE condicional — a verificação de saldo e a
/// alteração acontecem no mesmo comando, então duas requisições concorrentes nunca reservam a mesma unidade.
/// O Wolverine abre a transação antes do handler (AutoApplyTransactions): SQL, inserts do EF e eventos de saída
/// (outbox) são confirmados juntos. O RLS restringe tudo ao tenant do envelope.
/// </summary>
public static class StockHandler
{
    // Colunas em snake_case: a convenção de nomes do EF também vale para SqlQuery de tipos não mapeados.
    private sealed record ReservationRow(Guid PartId, int Quantity);

    public static async Task<(ReserveStockResult, OutgoingMessages)> Handle(
        ReserveStock command, TenantDbContext db, TimeProvider clock, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(command.Quantity);
        var outgoing = new OutgoingMessages();

        var available = await SingleOrNullAsync(db.Database.SqlQuery<int>($"""
            UPDATE stocks SET reserved = reserved + {command.Quantity}
            WHERE part_id = {command.PartId} AND on_hand - reserved >= {command.Quantity}
            RETURNING on_hand - reserved AS "Value"
            """), ct);
        if (available is null) return (ReserveStockResult.Unavailable, outgoing);

        var minutes = await db.StoreSettings.Select(s => (int?)s.ReservationMinutes).SingleOrDefaultAsync(ct)
            ?? StoreSettings.DefaultReservationMinutes;
        var now = clock.GetUtcNow();
        var reservation = new StockReservation(db.RequireTenantId(), command.PartId, command.Quantity, command.Reference, now, now.AddMinutes(minutes));
        db.StockReservations.Add(reservation);

        outgoing.Add(new StockChanged(command.PartId, available.Value));
        outgoing.Schedule(new ExpireReservation(reservation.Id), reservation.ExpiresAt);
        return (new ReserveStockResult(true, reservation.Id, reservation.ExpiresAt), outgoing);
    }

    public static async Task<(ConfirmReservationOutcome, OutgoingMessages)> Handle(
        ConfirmReservation command, TenantDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        var now = clock.GetUtcNow();

        var converted = await SingleOrNullAsync(db.Database.SqlQuery<ReservationRow>($"""
            UPDATE stock_reservations SET status = {nameof(ReservationStatus.Converted)}
            WHERE id = {command.ReservationId} AND status = {nameof(ReservationStatus.Active)}
            RETURNING part_id, quantity
            """), ct);

        if (converted is not null)
        {
            var available = await ExactlyOneAsync(db.Database.SqlQuery<int>($"""
                UPDATE stocks SET on_hand = on_hand - {converted.Quantity}, reserved = reserved - {converted.Quantity}
                WHERE part_id = {converted.PartId}
                RETURNING on_hand - reserved AS "Value"
                """), ct);
            AddMovement(db, converted, StockMovementReason.ReservationConverted, command.ReservationId, now);
            outgoing.Add(new StockChanged(converted.PartId, available));
            return (ConfirmReservationOutcome.Confirmed, outgoing);
        }

        var reservation = await db.StockReservations.AsNoTracking().SingleOrDefaultAsync(r => r.Id == command.ReservationId, ct);
        if (reservation is null) return (ConfirmReservationOutcome.NotFound, outgoing);
        if (reservation.Status == ReservationStatus.Converted) return (ConfirmReservationOutcome.AlreadyConfirmed, outgoing);

        // Pagamento chegou depois do prazo (RF12 CA3): tenta uma baixa direta com o saldo que ainda existir.
        var afterExpiry = await TrySellAsync(db, reservation.PartId, reservation.Quantity, ct);
        if (afterExpiry is null) return (ConfirmReservationOutcome.OutOfStock, outgoing);

        await db.Database.ExecuteSqlAsync($"""
            UPDATE stock_reservations SET status = {nameof(ReservationStatus.Converted)} WHERE id = {reservation.Id}
            """, ct);
        AddMovement(db, new ReservationRow(reservation.PartId, reservation.Quantity), StockMovementReason.SaleAfterExpiry, reservation.Id, now);
        outgoing.Add(new StockChanged(reservation.PartId, afterExpiry.Value));
        return (ConfirmReservationOutcome.ConfirmedAfterExpiry, outgoing);
    }

    public static Task<OutgoingMessages> Handle(ReleaseReservation command, TenantDbContext db, CancellationToken ct) =>
        EndReservationAsync(db, command.ReservationId, ReservationStatus.Released, expiredBefore: null, ct);

    public static Task<OutgoingMessages> Handle(ExpireReservation command, TenantDbContext db, TimeProvider clock, CancellationToken ct) =>
        EndReservationAsync(db, command.ReservationId, ReservationStatus.Expired, expiredBefore: clock.GetUtcNow(), ct);

    public static async Task<(SellStockResult, OutgoingMessages)> Handle(
        SellStock command, TenantDbContext db, TimeProvider clock, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(command.Quantity);
        var outgoing = new OutgoingMessages();

        var available = await TrySellAsync(db, command.PartId, command.Quantity, ct);
        if (available is null) return (new SellStockResult(false), outgoing);

        db.StockMovements.Add(new StockMovement(db.RequireTenantId(), command.PartId, -command.Quantity,
            StockMovementReason.DirectSale, command.Reference, reservationId: null, clock.GetUtcNow()));
        outgoing.Add(new StockChanged(command.PartId, available.Value));
        return (new SellStockResult(true), outgoing);
    }

    /// <summary>Encerra a reserva ativa e devolve o saldo; também usado ao cancelar um pedido (todas as reservas dele).</summary>
    internal static async Task<OutgoingMessages> EndReservationAsync(
        TenantDbContext db, Guid reservationId, ReservationStatus newStatus, DateTimeOffset? expiredBefore, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();

        // Só reservas ainda ativas: repetir a mensagem (ou expirar algo já pago) não tem efeito.
        var ended = await SingleOrNullAsync(expiredBefore is { } deadline
            ? db.Database.SqlQuery<ReservationRow>($"""
                UPDATE stock_reservations SET status = {newStatus.ToString()}
                WHERE id = {reservationId} AND status = {nameof(ReservationStatus.Active)} AND expires_at <= {deadline}
                RETURNING part_id, quantity
                """)
            : db.Database.SqlQuery<ReservationRow>($"""
                UPDATE stock_reservations SET status = {newStatus.ToString()}
                WHERE id = {reservationId} AND status = {nameof(ReservationStatus.Active)}
                RETURNING part_id, quantity
                """), ct);
        if (ended is null) return outgoing;

        var available = await ExactlyOneAsync(db.Database.SqlQuery<int>($"""
            UPDATE stocks SET reserved = reserved - {ended.Quantity}
            WHERE part_id = {ended.PartId}
            RETURNING on_hand - reserved AS "Value"
            """), ct);
        outgoing.Add(new StockChanged(ended.PartId, available));
        return outgoing;
    }

    private static Task<int?> TrySellAsync(TenantDbContext db, Guid partId, int quantity, CancellationToken ct) =>
        SingleOrNullAsync(db.Database.SqlQuery<int>($"""
            UPDATE stocks SET on_hand = on_hand - {quantity}
            WHERE part_id = {partId} AND on_hand - reserved >= {quantity}
            RETURNING on_hand - reserved AS "Value"
            """), ct);

    private static void AddMovement(TenantDbContext db, ReservationRow row, StockMovementReason reason, Guid reservationId, DateTimeOffset now) =>
        db.StockMovements.Add(new StockMovement(db.RequireTenantId(), row.PartId, -row.Quantity, reason,
            reservationId.ToString(), reservationId, now));

    // UPDATE ... RETURNING não pode ser composto pelo EF (FirstOrDefault viraria subconsulta); materializa e pega o único.
    private static async Task<int?> SingleOrNullAsync(IQueryable<int> query, CancellationToken ct)
    {
        var rows = await query.ToListAsync(ct);
        return rows.Count == 0 ? null : rows[0];
    }

    private static async Task<int> ExactlyOneAsync(IQueryable<int> query, CancellationToken ct) =>
        (await query.ToListAsync(ct)).Single();

    private static async Task<ReservationRow?> SingleOrNullAsync(IQueryable<ReservationRow> query, CancellationToken ct)
    {
        var rows = await query.ToListAsync(ct);
        return rows.Count == 0 ? null : rows[0];
    }
}
