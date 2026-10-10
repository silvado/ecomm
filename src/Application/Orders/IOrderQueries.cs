using Ecommerce.Domain.Orders;

namespace Ecommerce.Application.Orders;

public sealed record OrderSummary(
    Guid Id, long Number, OrderOrigin Origin, OrderStatus Status, DateTimeOffset PlacedAt, string BuyerName,
    DeliveryMethod DeliveryMethod, int ItemCount, decimal Total);

public sealed record OrderItemView(Guid PartId, string InternalCode, string Title, decimal UnitPrice, int Quantity);

public sealed record OrderBuyerView(string Name, string Email, string Phone, string Cpf);

public sealed record OrderView(
    Guid Id, long Number, OrderOrigin Origin, OrderStatus Status, DateTimeOffset PlacedAt, DateTimeOffset PaymentDeadline,
    OrderBuyerView Buyer, DeliveryMethod DeliveryMethod, DeliveryAddress? Address, string? Carrier, string? Service, int? DeliveryDays,
    string? PickupAddress, IReadOnlyList<OrderItemView> Items, decimal ItemsTotal, decimal ShippingTotal, decimal Total,
    DateTimeOffset? CanceledAt, string? CancelReason);

public sealed record OrderSearch(OrderStatus? Status, int Page = 1, int PageSize = 25)
{
    public const int MaxPageSize = 100;
}

public sealed record OrderPage(IReadOnlyList<OrderSummary> Items, int Total, int Page, int PageSize);

/// <summary>Pedidos da loja do escopo no painel (RLS + filtro global). Lista completa com filtros é o RF17.</summary>
public interface IOrderQueries
{
    Task<OrderPage> SearchAsync(OrderSearch search, CancellationToken ct = default);

    Task<OrderView?> GetAsync(Guid orderId, CancellationToken ct = default);
}
