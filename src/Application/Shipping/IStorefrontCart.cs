using Ecommerce.Domain.Catalog;

namespace Ecommerce.Application.Shipping;

/// <summary>Item do carrinho como o navegador guarda: só a peça e a quantidade (o resto vem sempre do servidor).</summary>
public sealed record CartItem(Guid PartId, int Quantity);

public enum CartProblem
{
    /// <summary>Peça removida, inativa ou de outra loja.</summary>
    Unavailable,
    /// <summary>Pediram mais do que há: a quantidade foi reduzida ao disponível.</summary>
    QuantityReduced,
    /// <summary>Sem medidas/peso da embalagem: não tem frete calculado (RF08 CA4), só retirada.</summary>
    NoShippingDimensions,
}

public sealed record CartLine(
    Guid PartId, string Title, PartCondition Condition, decimal UnitPrice, int Quantity, int Available, Guid? CoverPhotoId, CartProblem? Problem);

public sealed record CartView(IReadOnlyList<CartLine> Lines, decimal Subtotal);

/// <param name="Pickup">Endereço de retirada no balcão, se a loja oferece.</param>
/// <param name="Message">Por que não há entrega (pronta para o comprador), quando é o caso.</param>
public sealed record ShippingQuote(IReadOnlyList<ShippingOption> Options, string? Pickup, string? Message);

/// <summary>
/// Carrinho da loja pública (RF14). O navegador só guarda peça e quantidade; preço, estoque e frete são sempre
/// recalculados aqui, no tenant do Host.
/// </summary>
public interface IStorefrontCart
{
    public const int MaxLines = 50;

    Task<CartView> CheckAsync(IReadOnlyList<CartItem> items, CancellationToken ct = default);

    /// <summary>Erro de CEP vem como mensagem; falha do provedor deixa só a retirada (se houver).</summary>
    Task<(ShippingQuote? Quote, string? Error)> QuoteAsync(string destinationPostalCode, IReadOnlyList<CartItem> items, CancellationToken ct = default);
}
