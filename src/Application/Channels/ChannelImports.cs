using Ecommerce.Domain.Catalog;
using Ecommerce.Domain.Channels;

namespace Ecommerce.Application.Channels;

// Importação dos anúncios que a loja já tem no Mercado Livre (RF11 CA2/CA3): lista os ids em páginas pela fila do tenant
// e importa em lotes, com o progresso salvo junto de cada lote.

/// <summary>Começa a importação na conta do Mercado Livre conectada da loja (pedido do painel, síncrono).</summary>
public sealed record StartChannelImport(Guid UserId);

/// <summary>Lê a próxima página da lista de anúncios; enquanto houver página, encadeia outra.</summary>
public sealed record ListChannelImportPage(Guid ImportId);

/// <summary>Importa o próximo lote; enquanto houver anúncios, encadeia outro.</summary>
public sealed record ProcessChannelImport(Guid ImportId);

/// <summary>
/// Anúncio do vendedor lido do canal (RF11 CA2): o que interessa para virar peça e para o vínculo. <paramref name="Condition"/>
/// é o campo <c>condition</c> do ML ("new", "used", "not_specified"); <paramref name="ShippingDimensions"/>, o <c>shipping.dimensions</c>.
/// </summary>
public sealed record ChannelItemSnapshot(
    string Id, string Title, decimal Price, int Quantity, string Status, string? Condition, string? SellerCustomField, string? Description,
    IReadOnlyList<string> PictureUrls, string? CategoryId, string? ListingTypeId, IReadOnlyList<AttributeValue> Attributes,
    IReadOnlyList<AttributeValue> SaleTerms, IReadOnlyList<ChannelItemVariation> Variations, string? ShippingDimensions = null)
{
    public bool HasVariations => Variations.Count > 0;
}

/// <summary>
/// Variação do anúncio (ex.: lado esquerdo e lado direito): a combinação que a distingue, preço, saldo, fotos (já como
/// endereços) e o SKU próprio (atributo SELLER_SKU ou <c>seller_custom_field</c> da variação).
/// </summary>
public sealed record ChannelItemVariation(
    string Id, IReadOnlyList<AttributeValue> Combinations, decimal Price, int Quantity, IReadOnlyList<string> PictureUrls, string? SellerCustomField,
    IReadOnlyList<AttributeValue> Attributes);

/// <summary>Variação pronta para virar peça em rascunho (<see cref="Item"/>) ou, sem ela, o motivo.</summary>
public sealed record ImportedVariation(string VariationId, string Label, ImportedItem? Item, IReadOnlyList<string> PictureUrls, string? Reason);

/// <summary>Anúncio pronto para virar peça: código, dados, saldo e a situação do vínculo.</summary>
public sealed record ImportedItem(string InternalCode, PartDetails Details, int Quantity, ListingStatus ListingStatus, MercadoLivreListingOptions Options);

public sealed record ChannelImportView(
    Guid Id, ChannelImportStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt, int TotalItems, bool Truncated, int ProcessedItems,
    int CreatedCount, int LinkedCount, int AlreadyLinkedCount, int RejectedCount, int CompatibilitiesCount, IReadOnlyList<ChannelImportIssue> Issues,
    string? FailureReason);

public enum StartChannelImportOutcome
{
    Started,
    /// <summary>A loja não tem conta do Mercado Livre conectada (ou ela pede nova autorização).</summary>
    NotConnected,
    /// <summary>Já há uma importação em andamento: devolve a que está rodando.</summary>
    AlreadyRunning,
}

public sealed record StartChannelImportResult(StartChannelImportOutcome Outcome, Guid? ImportId);

/// <summary>Importações de anúncios da loja do escopo (RLS + filtro global). O início é a mensagem <see cref="StartChannelImport"/>.</summary>
public interface IChannelImports
{
    Task<ChannelImportView?> GetAsync(Guid importId, CancellationToken ct = default);

    /// <summary>As mais recentes primeiro (para acompanhar depois de recarregar a página).</summary>
    Task<IReadOnlyList<ChannelImportView>> RecentAsync(int count, CancellationToken ct = default);
}

/// <summary>
/// Regras fixas de como um anúncio do Mercado Livre vira peça (RF11 CA2). Código: o SKU do anúncio (SELLER_SKU ou
/// <c>seller_custom_field</c>) ou, sem SKU, o próprio id do anúncio. Pausado lá entra como pausado pelo lojista (o sistema
/// não reativa). Encerrado ou em revisão não entra. Anúncio com variações passa por <see cref="MapVariations"/>.
/// </summary>
public static class ChannelImportRules
{
    public const string SkuAttribute = "SELLER_SKU";

    /// <summary>Atributos que não voltam para as opções do anúncio (o SKU é o código da peça; os de leitura o ML calcula).</summary>
    private static readonly HashSet<string> NotEditable = new(StringComparer.Ordinal) { SkuAttribute, "PRODUCT_DATA_SOURCE" };

    /// <returns>A peça, ou o motivo para o relatório.</returns>
    public static (ImportedItem? Item, string? Reason) Map(ChannelItemSnapshot item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.HasVariations) return (null, "Anúncio com variações: cada variação vira uma peça.");
        if (ListingStatusOf(item) is not { } status) return (null, Closed);
        if (item.Price <= 0) return (null, "Anúncio sem preço.");
        var code = CodeOrNull(Value(item.Attributes, SkuAttribute) ?? item.SellerCustomField) ?? Part.NormalizeInternalCode(item.Id);
        return (new ImportedItem(code, Details(item, item.Title.Trim(), item.Price), Math.Max(0, item.Quantity), status, Options(item)), null);
    }

    /// <summary>
    /// Cada variação do anúncio vira uma peça (Q15: sem variações, cada variação é uma peça): título do anúncio com a
    /// combinação ("Farol Gol - Esquerdo"), preço, saldo e fotos da variação (sem fotos próprias, as do anúncio) e o resto
    /// do anúncio. Código: o SKU da variação ou, sem ele, o código do anúncio com o id da variação. As peças não são
    /// ligadas ao anúncio (o vínculo é com o anúncio inteiro, não com a variação) e entram em rascunho.
    /// </summary>
    /// <returns>As variações, ou o motivo de o anúncio inteiro ficar de fora.</returns>
    public static (IReadOnlyList<ImportedVariation> Variations, string? Reason) MapVariations(ChannelItemSnapshot item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.HasVariations) return ([], "Anúncio sem variações.");
        if (ListingStatusOf(item) is not { } status) return ([], Closed);

        var baseCode = CodeOrNull(Value(item.Attributes, SkuAttribute) ?? item.SellerCustomField) ?? item.Id;
        var options = Options(item);
        var result = new List<ImportedVariation>();
        foreach (var variation in item.Variations)
        {
            var label = string.Join(" / ", variation.Combinations.Select(c => c.ValueName?.Trim()).Where(n => n is { Length: > 0 }));
            if (label.Length == 0) label = variation.Id;
            var pictures = variation.PictureUrls.Count > 0 ? variation.PictureUrls : item.PictureUrls;
            var price = variation.Price > 0 ? variation.Price : item.Price;
            if (price <= 0)
            {
                result.Add(new ImportedVariation(variation.Id, label, null, pictures, "variação sem preço."));
                continue;
            }
            var code = CodeOrNull(Value(variation.Attributes, SkuAttribute) ?? variation.SellerCustomField)
                ?? CodeOrNull($"{baseCode}-{variation.Id}") ?? Part.NormalizeInternalCode($"{item.Id}-{variation.Id}");
            var suffix = $" - {label}";
            var title = item.Title.Trim();
            title = suffix.Length >= Part.TitleMaxLength ? (title + suffix)[..Part.TitleMaxLength]
                : (title.Length + suffix.Length > Part.TitleMaxLength ? title[..(Part.TitleMaxLength - suffix.Length)].TrimEnd() : title) + suffix;
            result.Add(new ImportedVariation(variation.Id, label,
                new ImportedItem(code, Details(item, title, price), Math.Max(0, variation.Quantity), status, options), pictures, null));
        }
        return (result, null);
    }

    private const string Closed = "Anúncio encerrado ou em revisão no Mercado Livre.";

    private static ListingStatus? ListingStatusOf(ChannelItemSnapshot item) => item.Status switch
    {
        "active" => ListingStatus.Active,
        "paused" => ListingStatus.PausedByUser,
        _ => null,
    };

    /// <summary>O código normalizado, ou nulo se vazio ou fora do limite da peça.</summary>
    private static string? CodeOrNull(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        try
        {
            return Part.NormalizeInternalCode(code);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static PartDetails Details(ChannelItemSnapshot item, string title, decimal price)
    {
        if (title.Length > Part.TitleMaxLength) title = title[..Part.TitleMaxLength].TrimEnd();
        var description = item.Description?.Trim();
        if (description is { Length: > Part.DescriptionMaxLength }) description = description[..Part.DescriptionMaxLength];
        var oem = Value(item.Attributes, "PART_NUMBER") ?? Value(item.Attributes, "OEM");
        var oemCodes = oem is { Length: > 0 and <= PartOemCode.MaxLength } ? new[] { oem.Trim() } : [];
        var package = PackageDimensions(item);
        return new PartDetails(title, string.IsNullOrEmpty(description) ? null : description, Condition(item), price,
            package?.LengthCm, package?.WidthCm, package?.HeightCm, package?.WeightG, oemCodes);
    }

    private static MercadoLivreListingOptions Options(ChannelItemSnapshot item) => new(
        item.CategoryId ?? string.Empty, item.ListingTypeId ?? "gold_special",
        item.Attributes.Where(a => !NotEditable.Contains(a.Id) && (a.ValueId is { Length: > 0 } || a.ValueName is { Length: > 0 })).ToList(),
        Value(item.SaleTerms, "WARRANTY_TYPE") is { } warranty && !warranty.Equals("Sem garantia", StringComparison.OrdinalIgnoreCase) ? warranty : null,
        Value(item.SaleTerms, "WARRANTY_TIME"));

    /// <summary>Medidas da embalagem para o frete: cm e gramas inteiros (arredondados para cima, para a caixa caber).</summary>
    public sealed record Package(int LengthCm, int WidthCm, int HeightCm, int WeightG);

    /// <summary>
    /// Medidas da embalagem do anúncio: os atributos SELLER_PACKAGE_LENGTH/WIDTH/HEIGHT (cm) e SELLER_PACKAGE_WEIGHT (g)
    /// ou, sem eles, o <c>shipping.dimensions</c> no formato "AxBxC,peso" (cm e g). Fonte:
    /// https://developers.mercadolivre.com.br/pt_br/itens-atributos-de-envio-e-dimensoes. HIPÓTESE: no
    /// <c>shipping.dimensions</c>, a ordem é altura x largura x comprimento (a página não diz). Só vale com as quatro medidas
    /// dentro dos limites da peça — senão, nenhuma (medida pela metade não calcula frete).
    /// </summary>
    public static Package? PackageDimensions(ChannelItemSnapshot item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var fromAttributes = (Measure(Value(item.Attributes, "SELLER_PACKAGE_LENGTH"), Length), Measure(Value(item.Attributes, "SELLER_PACKAGE_WIDTH"), Length),
            Measure(Value(item.Attributes, "SELLER_PACKAGE_HEIGHT"), Length), Measure(Value(item.Attributes, "SELLER_PACKAGE_WEIGHT"), Weight));
        if (fromAttributes is ({ } l, { } w, { } h, { } g)) return Valid(l, w, h, g);

        // "10x20x30,500"
        var parts = (item.ShippingDimensions ?? string.Empty).Split(',', 2, StringSplitOptions.TrimEntries);
        var sides = parts[0].Split('x', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || sides.Length != 3) return null;
        return (Measure(sides[2], Length), Measure(sides[1], Length), Measure(sides[0], Length), Measure(parts[1], Weight)) is ({ } sl, { } sw, { } sh, { } sg)
            ? Valid(sl, sw, sh, sg)
            : null;
    }

    private static readonly Dictionary<string, decimal> Length = new(StringComparer.OrdinalIgnoreCase) { [""] = 1, ["cm"] = 1, ["mm"] = 0.1m, ["m"] = 100 };
    private static readonly Dictionary<string, decimal> Weight = new(StringComparer.OrdinalIgnoreCase) { [""] = 1, ["g"] = 1, ["kg"] = 1000 };

    /// <summary>"20", "20 cm", "1,5 kg", "500g" → na unidade base; nulo se não for número com unidade conhecida.</summary>
    private static decimal? Measure(string? text, Dictionary<string, decimal> units)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text?.Trim() ?? string.Empty, @"^(\d{1,9}(?:[.,]\d{1,6})?)\s*([a-zA-Z]*)$");
        if (!match.Success || !units.TryGetValue(match.Groups[2].Value, out var factor)) return null;
        return decimal.Parse(match.Groups[1].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture) * factor;
    }

    private static Package? Valid(decimal length, decimal width, decimal height, decimal weight)
    {
        var (l, w, h, g) = ((int)Math.Ceiling(Math.Min(length, int.MaxValue)), (int)Math.Ceiling(Math.Min(width, int.MaxValue)),
            (int)Math.Ceiling(Math.Min(height, int.MaxValue)), (int)Math.Ceiling(Math.Min(weight, int.MaxValue)));
        return new[] { l, w, h }.All(d => d is > 0 and <= Part.MaxDimensionCm) && g is > 0 and <= Part.MaxWeightG ? new Package(l, w, h, g) : null;
    }

    private static PartCondition Condition(ChannelItemSnapshot item)
    {
        if (Value(item.Attributes, "ITEM_CONDITION") is { } name && name.StartsWith("Recondicionad", StringComparison.OrdinalIgnoreCase))
            return PartCondition.Refurbished;
        return item.Condition == "new" ? PartCondition.New : PartCondition.Used;
    }

    private static string? Value(IEnumerable<AttributeValue> attributes, string id) =>
        attributes.FirstOrDefault(a => a.Id == id)?.ValueName is { } v && v.Trim().Length > 0 ? v.Trim() : null;
}
