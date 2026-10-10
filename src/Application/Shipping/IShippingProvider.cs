using Ecommerce.Domain.Shipping;

namespace Ecommerce.Application.Shipping;

/// <summary>Um volume para a cotação: medidas da embalagem (cm), peso (kg) e valor segurado (BRL, por unidade).</summary>
public sealed record ShippingPackage(string Id, int WidthCm, int HeightCm, int LengthCm, decimal WeightKg, decimal InsuranceValue, int Quantity);

public sealed record ShippingQuoteRequest(PostalCode From, PostalCode To, IReadOnlyList<ShippingPackage> Packages);

/// <summary>Uma forma de entrega cotada (ex.: Correios SEDEX, R$ 32,40, 3 dias úteis).</summary>
public sealed record ShippingOption(string ServiceId, string Carrier, string Service, decimal Price, int DeliveryDays);

/// <summary>Falha do provedor de frete (credencial, indisponibilidade). A mensagem nunca leva segredo.</summary>
public sealed class ShippingProviderException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Cotação de frete (RF14 CA1). Implementações: Melhor Envio e fake (dev/testes) — CLAUDE.md, regra 6.</summary>
public interface IShippingProvider
{
    /// <summary>Só as opções que o provedor consegue entregar; lança <see cref="ShippingProviderException"/> em falha.</summary>
    Task<IReadOnlyList<ShippingOption>> QuoteAsync(ShippingQuoteRequest request, CancellationToken ct = default);
}

/// <summary>Escolhe o provedor da loja do escopo (ex.: Melhor Envio conectado); nulo = loja sem entrega, só retirada.</summary>
public interface IShippingProviderResolver
{
    Task<IShippingProvider?> ResolveAsync(CancellationToken ct = default);
}
