using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Inventory;

public enum ReservationStatus
{
    Active,
    /// <summary>Pagamento confirmado: virou baixa definitiva.</summary>
    Converted,
    /// <summary>Prazo esgotado sem pagamento.</summary>
    Expired,
    /// <summary>Cancelada antes do prazo (ex.: comprador desistiu).</summary>
    Released,
}

/// <summary>Reserva de estoque para um pedido não pago (RF12).</summary>
public sealed class StockReservation : ITenantOwned
{
    private StockReservation() { }

    public StockReservation(Guid tenantId, Guid partId, int quantity, string reference, DateTimeOffset now, DateTimeOffset expiresAt)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (expiresAt <= now) throw new ArgumentException("A reserva precisa expirar no futuro.", nameof(expiresAt));

        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        PartId = partId;
        Quantity = quantity;
        Reference = reference;
        Status = ReservationStatus.Active;
        CreatedAt = now;
        ExpiresAt = expiresAt;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid PartId { get; private set; }
    public int Quantity { get; private set; }

    /// <summary>Quem segura a peça: id do pedido/checkout.</summary>
    public string Reference { get; private set; } = string.Empty;

    public ReservationStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
}
