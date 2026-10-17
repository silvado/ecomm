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
    /// <summary>Era baixa (pedido pago) e a loja cancelou o pedido: a peça voltou ao estoque (RF17).</summary>
    Returned,
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

    /// <summary>
    /// Baixa direta já feita (venda de balcão, RF18): nasce convertida, sem prazo. Ligada à linha do pedido, permite
    /// devolver a peça ao estoque se a venda for cancelada.
    /// </summary>
    /// <summary>
    /// Venda de canal que chegou sem saldo (conflito de estoque — Q01): nada foi baixado, então a "reserva" nasce liberada
    /// (cancelar o pedido não devolve nada). Se a loja confirmar a venda, a baixa entra como direta, se houver saldo.
    /// </summary>
    public static StockReservation Unfulfilled(Guid tenantId, Guid partId, int quantity, string reference, DateTimeOffset now)
    {
        var reservation = Sold(tenantId, partId, quantity, reference, now);
        reservation.Status = ReservationStatus.Released;
        return reservation;
    }

    public static StockReservation Sold(Guid tenantId, Guid partId, int quantity, string reference, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        return new StockReservation
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            PartId = partId,
            Quantity = quantity,
            Reference = reference,
            Status = ReservationStatus.Converted,
            CreatedAt = now,
            ExpiresAt = now,
        };
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
