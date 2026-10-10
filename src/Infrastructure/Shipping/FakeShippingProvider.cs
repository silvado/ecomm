using Ecommerce.Application.Shipping;

namespace Ecommerce.Infrastructure.Shipping;

/// <summary>
/// Fake de <see cref="IShippingProvider"/> (dev e testes; CLAUDE.md, regra 6). Determinístico: o preço sobe com o
/// peso e com a "distância" entre as regiões dos CEPs (primeiro dígito).
/// </summary>
public sealed class FakeShippingProvider : IShippingProvider
{
    public Task<IReadOnlyList<ShippingOption>> QuoteAsync(ShippingQuoteRequest request, CancellationToken ct = default)
    {
        var weightKg = request.Packages.Sum(p => p.WeightKg * p.Quantity);
        var regions = Math.Abs(request.From.Value[0] - request.To.Value[0]);
        var basePrice = 18m + 3.5m * Math.Ceiling(weightKg) + 4m * regions;
        IReadOnlyList<ShippingOption> options =
        [
            new("fake-economico", "Transportadora Teste", "Econômico", decimal.Round(basePrice, 2), 6 + regions),
            new("fake-expresso", "Transportadora Teste", "Expresso", decimal.Round(basePrice * 1.8m, 2), 2 + regions / 2),
        ];
        return Task.FromResult(options);
    }
}
