using Ecommerce.Domain.Orders;
using Ecommerce.Domain.Payments;

namespace Ecommerce.Application.Orders;

public sealed record OrderSummary(
    Guid Id, long Number, OrderOrigin Origin, OrderStatus Status, DateTimeOffset PlacedAt, string BuyerName,
    DeliveryMethod DeliveryMethod, int ItemCount, decimal Total);

/// <param name="ReturnedQuantity">Quanto desta peça já voltou em devoluções (RF20).</param>
public sealed record OrderItemView(Guid PartId, string InternalCode, string Title, decimal UnitPrice, int Quantity, int ReturnedQuantity);

/// <summary>Devolução registrada (RF20): peças, se voltaram ao estoque, motivo e reembolso informado.</summary>
public sealed record OrderReturnView(DateTimeOffset At, ReturnReason Reason, string? Note, decimal? RefundAmount, RefundMethod? RefundMethod,
    string? UserEmail, IReadOnlyList<OrderReturnLine> Lines);

/// <summary>Na venda de balcão, e-mail, telefone e CPF podem vir vazios e o documento pode ser CNPJ.</summary>
public sealed record OrderBuyerView(string Name, string Email, string Phone, string Cpf, string? Cnpj);

public sealed record OrderPaymentView(string Gateway, string ExternalId, PaymentStatus Status, decimal Amount, string? Method, DateTimeOffset UpdatedAt);

/// <param name="UserEmail">E-mail de quem fez, quando foi um usuário da loja.</param>
public sealed record OrderHistoryView(DateTimeOffset At, OrderStatus? From, OrderStatus To, OrderActor Actor, string? UserEmail, string? Note);

public sealed record OrderView(
    Guid Id, long Number, OrderOrigin Origin, string? ExternalId, OrderStatus Status, DateTimeOffset PlacedAt, DateTimeOffset PaymentDeadline,
    OrderBuyerView Buyer, DeliveryMethod DeliveryMethod, DeliveryAddress? Address, string? Carrier, string? Service, int? DeliveryDays,
    string? PickupAddress, IReadOnlyList<OrderItemView> Items, decimal ItemsTotal, decimal ShippingTotal, decimal Total,
    DateTimeOffset? CanceledAt, string? CancelReason, CancellationSource? CanceledBy, DateTimeOffset? PaidAt, string? ReviewReason,
    string? TrackingCode, DateTimeOffset? ShippedAt, DateTimeOffset? DeliveredAt, CounterPaymentMethod? PaymentMethod,
    IReadOnlyList<OrderPaymentView> Payments, IReadOnlyList<OrderHistoryView> History, DateTimeOffset? ReturnedAt, IReadOnlyList<OrderReturnView> Returns);

/// <summary>
/// Filtros do painel unificado (RF17 CA1). <paramref name="From"/>/<paramref name="To"/>: dias (inclusive) no fuso da loja
/// (America/Sao_Paulo). <paramref name="Text"/>: número do pedido, nome, e-mail ou CPF do comprador.
/// </summary>
public sealed record OrderSearch(
    IReadOnlyList<OrderStatus>? Statuses = null, OrderOrigin? Origin = null, DateOnly? From = null, DateOnly? To = null, string? Text = null,
    int Page = 1, int PageSize = 25)
{
    public const int MaxPageSize = 100;
    public const int MaxTextLength = 100;
}

public sealed record OrderPage(IReadOnlyList<OrderSummary> Items, int Total, int Page, int PageSize);

/// <summary>Pedidos da loja do escopo no painel (RLS + filtro global): todos os canais num lugar só (RF17).</summary>
public interface IOrderQueries
{
    Task<OrderPage> SearchAsync(OrderSearch search, CancellationToken ct = default);

    Task<OrderView?> GetAsync(Guid orderId, CancellationToken ct = default);

    /// <summary>Pedido criado com este hash de token (idempotência do checkout e da venda de balcão).</summary>
    Task<Guid?> FindByAccessHashAsync(byte[] hash, CancellationToken ct = default);
}
