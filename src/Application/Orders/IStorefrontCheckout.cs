using Ecommerce.Application.Shipping;
using Ecommerce.Domain.Orders;

namespace Ecommerce.Application.Orders;

/// <summary>Entrega escolhida no checkout: <see cref="ServiceId"/> é uma opção que o servidor cotou para o CEP do endereço.</summary>
public sealed record CheckoutDelivery(DeliveryMethod Method, DeliveryAddress? Address, string? ServiceId);

/// <param name="Token">Sorteado pelo navegador (32 bytes em base64url): dá acesso ao acompanhamento e evita pedido duplicado.</param>
/// <param name="ExpectedTotal">Total que o comprador viu; se o servidor calcular outro, nada é criado e o comprador revê.</param>
public sealed record CheckoutRequest(string? Token, IReadOnlyList<CartItem>? Items, decimal? ExpectedTotal, BuyerDetails? Buyer, CheckoutDelivery? Delivery);

public enum CheckoutOutcome
{
    Placed,
    Invalid,
    /// <summary>Preço, estoque, frete ou total mudou desde o carrinho: o comprador precisa rever antes de confirmar.</summary>
    Changed,
}

public sealed record PlacedOrder(long Number, decimal Total, DateTimeOffset PaymentDeadline);

public sealed record CheckoutResult(CheckoutOutcome Outcome, string? Message, PlacedOrder? Order)
{
    public static CheckoutResult Invalid(string message) => new(CheckoutOutcome.Invalid, message, null);
    public static CheckoutResult Changed(string message) => new(CheckoutOutcome.Changed, message, null);
}

public sealed record StoreOrderItemView(Guid PartId, string Title, decimal UnitPrice, int Quantity);

/// <summary>O que o comprador vê pelo link do pedido: sem CPF, e-mail ou telefone.</summary>
public sealed record StoreOrderView(
    long Number, OrderStatus Status, DateTimeOffset PlacedAt, DateTimeOffset PaymentDeadline, string BuyerName,
    DeliveryMethod DeliveryMethod, DeliveryAddress? Address, string? Carrier, string? Service, int? DeliveryDays, string? PickupAddress,
    IReadOnlyList<StoreOrderItemView> Items, decimal ItemsTotal, decimal ShippingTotal, decimal Total, string? CancelReason);

/// <summary>Checkout como convidado na loja do Host (RF14 CA3) e acompanhamento do pedido pelo link com token.</summary>
public interface IStorefrontCheckout
{
    Task<CheckoutResult> PlaceAsync(CheckoutRequest request, CancellationToken ct = default);

    Task<StoreOrderView?> FindAsync(string? token, CancellationToken ct = default);
}
