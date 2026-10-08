namespace Ecommerce.Application.Inventory;

// Contratos das operações de estoque (RF12, RF18, RF20, RNF02). Enviados com o tenant no envelope
// (IMessageBus.InvokeForTenantAsync); os handlers ficam na Infrastructure, junto do SQL atômico.

/// <summary>Segura a quantidade para um pedido não pago, pelo prazo configurado da loja.</summary>
public sealed record ReserveStock(Guid PartId, int Quantity, string Reference);

public sealed record ReserveStockResult(bool Reserved, Guid? ReservationId, DateTimeOffset? ExpiresAt)
{
    public static readonly ReserveStockResult Unavailable = new(false, null, null);
}

/// <summary>Pagamento confirmado: converte a reserva em baixa definitiva.</summary>
public sealed record ConfirmReservation(Guid ReservationId);

public enum ConfirmReservationOutcome
{
    Confirmed,
    /// <summary>A reserva tinha expirado, mas ainda havia saldo: baixa feita mesmo assim.</summary>
    ConfirmedAfterExpiry,
    AlreadyConfirmed,
    /// <summary>A reserva expirou e a peça foi vendida a outro: o pedido precisa ser estornado (RF12 CA3).</summary>
    OutOfStock,
    NotFound,
}

/// <summary>Cancela uma reserva ativa (comprador desistiu, pagamento recusado).</summary>
public sealed record ReleaseReservation(Guid ReservationId);

/// <summary>Agendada para o fim do prazo da reserva; sem efeito se ela já foi paga ou cancelada.</summary>
public sealed record ExpireReservation(Guid ReservationId);

/// <summary>Baixa sem reserva: balcão, Mercado Livre, OLX.</summary>
public sealed record SellStock(Guid PartId, int Quantity, string Reference);

public sealed record SellStockResult(bool Sold);

/// <summary>Publicado a cada mudança de saldo disponível; consumido pela sincronização de canais (RF25).</summary>
public sealed record StockChanged(Guid PartId, int Available);
