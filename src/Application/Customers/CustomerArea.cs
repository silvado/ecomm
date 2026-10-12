using Ecommerce.Application.Orders;
using Ecommerce.Domain.Orders;

namespace Ecommerce.Application.Customers;

public sealed record CustomerOrderSummary(long Number, DateTimeOffset PlacedAt, OrderStatus Status, DeliveryMethod DeliveryMethod, int ItemCount,
    decimal Total, string? TrackingCode);

public enum StartSessionOutcome
{
    Started,
    /// <summary>Link vencido, já usado ou que não existe — a mesma resposta para os três.</summary>
    Invalid,
}

/// <summary>
/// Pedido de link pela fila de comandos (na transação: grava o link e agenda o e-mail juntos). Sem efeito se o e-mail não
/// tem pedidos na loja ou passou do limite de links por hora.
/// </summary>
public sealed record IssueCustomerAccessLink(string Email, string Origin);

public sealed record StartSessionResult(StartSessionOutcome Outcome, string? SessionToken = null, string? Email = null, DateTimeOffset? ExpiresAt = null);

/// <summary>
/// Área do cliente da loja do escopo (RF16): acesso só por link no e-mail (Q39), e o comprador vê só os pedidos do próprio
/// e-mail nesta loja (CA2). Tokens opacos; o banco guarda só o hash.
/// </summary>
public interface ICustomerArea
{
    /// <summary>
    /// Pede o link. Envia só se o e-mail tem pedidos nesta loja e não passou do limite de links por hora, mas a resposta
    /// para quem pediu é sempre a mesma (não revela quem comprou).
    /// </summary>
    Task RequestLinkAsync(string? email, string origin, CancellationToken ct = default);

    Task<StartSessionResult> StartSessionAsync(string? linkToken, CancellationToken ct = default);

    /// <summary>E-mail da sessão, se ela existe e está ativa.</summary>
    Task<string?> SessionEmailAsync(string? sessionToken, CancellationToken ct = default);

    Task<IReadOnlyList<CustomerOrderSummary>> OrdersAsync(string email, CancellationToken ct = default);

    Task<StoreOrderView?> OrderAsync(string email, long number, CancellationToken ct = default);

    Task EndSessionAsync(string? sessionToken, CancellationToken ct = default);
}
