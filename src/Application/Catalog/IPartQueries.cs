using Ecommerce.Domain.Catalog;

namespace Ecommerce.Application.Catalog;

public sealed record StockView(int OnHand, int Reserved, int Available);

public sealed record PhotoView(Guid Id, int Position);

/// <summary>
/// Anos efetivos (os da compatibilidade ou, sem eles, os da versão). <see cref="Narrowed"/>: restringe a versão.
/// <see cref="SuggestedByAi"/>: veio do cadastro por foto (RF30).
/// </summary>
public sealed record CompatibilityView(
    Guid Id, Guid VehicleVersionId, string Brand, string Model, string Engine, int YearFrom, int? YearTo, bool Narrowed, bool Discontinued,
    bool SuggestedByAi = false);

/// <summary>Filtro por veículo (RF09 CA3): modelo obrigatório; versão e ano-modelo opcionais.</summary>
public sealed record VehicleFilter(Guid ModelId, Guid? VersionId, int? Year);

/// <summary><see cref="CoverPhotoId"/>: primeira foto (miniatura da listagem), nula se a peça não tem fotos.</summary>
public sealed record PartSummary(
    Guid Id, string InternalCode, string Title, PartCondition Condition, decimal Price, PartStatus Status,
    StockView Stock, bool HasShippingDimensions, DateTimeOffset UpdatedAt, Guid? CoverPhotoId);

/// <summary>Origem da peça (RF10) — só no painel. <see cref="MissingForSale"/>: o que o modo desmontadora ainda exige.</summary>
public sealed record PartTraceabilityView(
    Guid? DonorVehicleId, string? DonorVehicleLabel, string? Tag, IReadOnlyDictionary<string, string> CustomFields, IReadOnlyList<string> MissingForSale);

public sealed record PartView(
    Guid Id, string InternalCode, string Title, string Description, PartCondition Condition, decimal Price,
    int? LengthCm, int? WidthCm, int? HeightCm, int? WeightG, IReadOnlyList<string> OemCodes,
    PartStatus Status, StockView Stock, bool HasShippingDimensions, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<PhotoView> Photos, IReadOnlyList<CompatibilityView> Compatibilities, PartTraceabilityView Traceability, PartFiscalView Fiscal);

/// <summary>Dados fiscais próprios da peça (RF19 CA3); nulos usam o padrão da loja.</summary>
public sealed record PartFiscalView(string? Ncm, string? TaxCode, int? Origin);

/// <param name="Search">Trecho do título ou do código interno, um código OEM (com ou sem espaços e hífens) ou a etiqueta de rastreabilidade.</param>
public sealed record PartSearch(string? Search, PartStatus? Status, int Page = 1, int PageSize = 25, VehicleFilter? Vehicle = null)
{
    public const int MaxPageSize = 100;
}

public sealed record PartPage(IReadOnlyList<PartSummary> Items, int Total, int Page, int PageSize);

/// <summary>Leituras do catálogo da loja do escopo (RLS + filtro global).</summary>
public interface IPartQueries
{
    Task<PartPage> SearchAsync(PartSearch search, CancellationToken ct = default);

    Task<PartView?> GetAsync(Guid partId, CancellationToken ct = default);
}
