using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Inventory;

public enum StockMovementReason
{
    /// <summary>Reserva paga.</summary>
    ReservationConverted,
    /// <summary>Pagamento chegou depois de a reserva expirar e ainda havia saldo (RF12 CA3).</summary>
    SaleAfterExpiry,
    /// <summary>Venda sem reserva: balcão, Mercado Livre, OLX.</summary>
    DirectSale,
}

/// <summary>Histórico de alterações do saldo físico (<see cref="Stock.OnHand"/>).</summary>
public sealed class StockMovement : ITenantOwned
{
    private StockMovement() { }

    public StockMovement(Guid tenantId, Guid partId, int delta, StockMovementReason reason, string reference, Guid? reservationId, DateTimeOffset at)
    {
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        PartId = partId;
        Delta = delta;
        Reason = reason;
        Reference = reference;
        ReservationId = reservationId;
        At = at;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid PartId { get; private set; }
    public int Delta { get; private set; }
    public StockMovementReason Reason { get; private set; }
    public string Reference { get; private set; } = string.Empty;
    public Guid? ReservationId { get; private set; }
    public DateTimeOffset At { get; private set; }
}
