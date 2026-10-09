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
    /// <summary>Saldo definido pelo lojista no cadastro ou na edição da peça (RF08).</summary>
    Adjustment,
}

/// <summary>Histórico de alterações do saldo físico (<see cref="Stock.OnHand"/>).</summary>
public sealed class StockMovement : ITenantOwned
{
    private StockMovement() { }

    public StockMovement(Guid tenantId, Guid partId, int delta, StockMovementReason reason, string reference, Guid? reservationId, DateTimeOffset at,
        Guid? userId = null)
    {
        UserId = userId;
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

    /// <summary>Quem fez o ajuste manual (nulo em movimentos automáticos: vendas, reservas).</summary>
    public Guid? UserId { get; private set; }
    public DateTimeOffset At { get; private set; }
}
