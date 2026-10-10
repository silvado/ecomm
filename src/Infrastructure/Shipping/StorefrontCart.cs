using Ecommerce.Application.Shipping;
using Ecommerce.Domain.Catalog;
using Ecommerce.Domain.Shipping;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Shipping;

/// <summary>
/// Carrinho da loja pública (RF14): confere peça, preço e estoque no tenant do Host (só peças ativas) e cota o frete
/// com as medidas da embalagem e o preço do servidor — nada do que o navegador manda além de peça e quantidade.
/// </summary>
public sealed class StorefrontCart(TenantDbContext db, IShippingProviderResolver providers) : IStorefrontCart
{
    private sealed record PartRow(Guid Id, string Title, PartCondition Condition, decimal Price, int Available,
        int? LengthCm, int? WidthCm, int? HeightCm, int? WeightG, Guid? Cover);

    public async Task<CartView> CheckAsync(IReadOnlyList<CartItem> items, CancellationToken ct = default)
    {
        var (lines, _) = await LoadAsync(items, ct);
        return new CartView(lines, lines.Where(l => l.Problem != CartProblem.Unavailable).Sum(l => l.UnitPrice * l.Quantity));
    }

    public async Task<(ShippingQuote? Quote, string? Error)> QuoteAsync(string destinationPostalCode, IReadOnlyList<CartItem> items, CancellationToken ct = default)
    {
        if (!PostalCode.TryParse(destinationPostalCode, out var destination)) return (null, "CEP inválido: informe os 8 dígitos.");

        var settings = await db.StoreSettings.AsNoTracking().SingleOrDefaultAsync(ct);
        var pickup = settings is { PickupEnabled: true } ? settings.PickupAddress : null;
        var (lines, parts) = await LoadAsync(items, ct);
        var shippable = lines.Where(l => l.Problem is null or CartProblem.QuantityReduced).ToList();

        if (shippable.Count == 0 && lines.Count > 0 && lines.All(l => l.Problem == CartProblem.Unavailable))
            return (new ShippingQuote([], pickup, "As peças do carrinho não estão mais disponíveis."), null);
        if (shippable.Any(l => !HasDimensions(parts[l.PartId])) || lines.Any(l => l.Problem == CartProblem.NoShippingDimensions))
            return (new ShippingQuote([], pickup, "Há peça sem medidas de embalagem no carrinho: entrega indisponível, só retirada na loja."), null);
        if (settings?.OriginPostalCode is not { } origin)
            return (new ShippingQuote([], pickup, "Esta loja ainda não faz entregas."), null);

        var provider = await providers.ResolveAsync(ct);
        if (provider is null) return (new ShippingQuote([], pickup, "Esta loja ainda não faz entregas."), null);

        var packages = shippable.Select(line =>
        {
            var part = parts[line.PartId];
            return new ShippingPackage(part.Id.ToString(), part.WidthCm!.Value, part.HeightCm!.Value, part.LengthCm!.Value,
                part.WeightG!.Value / 1000m, part.Price, line.Quantity);
        }).ToList();

        try
        {
            var options = await provider.QuoteAsync(new ShippingQuoteRequest(PostalCode.Parse(origin), destination, packages), ct);
            return (new ShippingQuote(options, pickup, options.Count == 0 ? "Nenhuma transportadora atende este CEP para estas peças." : null), null);
        }
        catch (ShippingProviderException)
        {
            // Falha do provedor não derruba a compra: o comprador ainda pode retirar, se a loja oferece.
            return (new ShippingQuote([], pickup, "Não foi possível calcular o frete agora. Tente de novo em instantes."), null);
        }
    }

    private async Task<(List<CartLine> Lines, Dictionary<Guid, PartRow> Parts)> LoadAsync(IReadOnlyList<CartItem> items, CancellationToken ct)
    {
        var wanted = items.Where(i => i.Quantity > 0).GroupBy(i => i.PartId)
            .Select(g => (PartId: g.Key, Quantity: g.Sum(i => i.Quantity))).Take(IStorefrontCart.MaxLines).ToList();
        var ids = wanted.Select(w => w.PartId).ToList();

        var parts = await (from p in db.Parts.AsNoTracking()
                           join s in db.Stocks on p.Id equals s.PartId
                           where ids.Contains(p.Id) && p.Status == PartStatus.Active
                           select new PartRow(p.Id, p.Title, p.Condition, p.Price, s.OnHand - s.Reserved, p.LengthCm, p.WidthCm, p.HeightCm, p.WeightG,
                               db.PartPhotos.Where(f => f.PartId == p.Id && f.Position == 0).Select(f => (Guid?)f.Id).FirstOrDefault()))
            .ToDictionaryAsync(p => p.Id, ct);

        var lines = wanted.Select(w =>
        {
            if (!parts.TryGetValue(w.PartId, out var part) || part.Available <= 0)
                return new CartLine(w.PartId, part?.Title ?? "Peça indisponível", part?.Condition ?? PartCondition.Used, part?.Price ?? 0, 0, 0, part?.Cover, CartProblem.Unavailable);
            var quantity = Math.Min(w.Quantity, part.Available);
            CartProblem? problem = quantity < w.Quantity ? CartProblem.QuantityReduced
                : !HasDimensions(part) ? CartProblem.NoShippingDimensions
                : null;
            return new CartLine(part.Id, part.Title, part.Condition, part.Price, quantity, part.Available, part.Cover, problem);
        }).ToList();
        return (lines, parts);
    }

    private static bool HasDimensions(PartRow part) =>
        part.LengthCm is not null && part.WidthCm is not null && part.HeightCm is not null && part.WeightG is not null;
}
