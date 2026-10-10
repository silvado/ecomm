using Ecommerce.Domain.Orders;

namespace Ecommerce.Application.Orders;

// Contratos do pedido da loja (RF14, RF12). Enviados com o tenant no envelope (IMessageBus.InvokeForTenantAsync);
// o handler fica na Infrastructure, junto do SQL atômico de reserva.

/// <summary>Item com o preço e o título já conferidos pelo servidor.</summary>
public sealed record PlaceOrderLine(Guid PartId, string InternalCode, string Title, decimal UnitPrice, int Quantity);

/// <summary>
/// Cria o pedido aguardando pagamento e reserva todas as peças na mesma transação — ou todas, ou nenhuma.
/// Sem <paramref name="Shipping"/>, é retirada em <paramref name="PickupAddress"/>.
/// </summary>
public sealed record PlaceOrder(byte[] AccessTokenHash, BuyerDetails Buyer, DeliveryAddress? Address, ShippingChoice? Shipping,
    string? PickupAddress, IReadOnlyList<PlaceOrderLine> Lines);

public enum PlaceOrderOutcome
{
    Placed,
    /// <summary>Alguma peça ficou sem saldo entre o carrinho e o envio; nada foi reservado.</summary>
    OutOfStock,
}

public sealed record PlaceOrderResult(PlaceOrderOutcome Outcome, long? Number, decimal? Total, DateTimeOffset? PaymentDeadline, Guid? UnavailablePartId)
{
    public static PlaceOrderResult Unavailable(Guid partId) => new(PlaceOrderOutcome.OutOfStock, null, null, null, partId);
}

/// <summary>Agendada para o fim do prazo de pagamento; sem efeito se o pedido já foi pago ou cancelado.</summary>
public sealed record ExpireOrder(Guid OrderId);
