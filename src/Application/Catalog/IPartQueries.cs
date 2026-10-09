using Ecommerce.Domain.Catalog;

namespace Ecommerce.Application.Catalog;

public sealed record StockView(int OnHand, int Reserved, int Available);

public sealed record PhotoView(Guid Id, int Position);

/// <summary>Anos efetivos (os da compatibilidade ou, sem eles, os da versão). <see cref="Narrowed"/>: restringe a versão.</summary>
public sealed record CompatibilityView(
    Guid Id, Guid VehicleVersionId, string Brand, string Model, string Engine, int YearFrom, int? YearTo, bool Narrowed, bool Discontinued);

/// <summary>Filtro por veículo (RF09 CA3): modelo obrigatório; versão e ano-modelo opcionais.</summary>
public sealed record VehicleFilter(Guid ModelId, Guid? VersionId, int? Year);

/// <summary>O que a loja pública mostra de uma peça ativa.</summary>
public sealed record StorePartSummary(Guid Id, string Title, PartCondition Condition, decimal Price, int Available, Guid? CoverPhotoId);

/// <summary><see cref="CoverPhotoId"/>: primeira foto (miniatura da listagem), nula se a peça não tem fotos.</summary>
public sealed record PartSummary(
    Guid Id, string InternalCode, string Title, PartCondition Condition, decimal Price, PartStatus Status,
    StockView Stock, bool HasShippingDimensions, DateTimeOffset UpdatedAt, Guid? CoverPhotoId);

public sealed record PartView(
    Guid Id, string InternalCode, string Title, string Description, PartCondition Condition, decimal Price,
    int? LengthCm, int? WidthCm, int? HeightCm, int? WeightG, IReadOnlyList<string> OemCodes,
    PartStatus Status, StockView Stock, bool HasShippingDimensions, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<PhotoView> Photos, IReadOnlyList<CompatibilityView> Compatibilities);

/// <param name="Search">Trecho do título ou do código interno, ou um código OEM (com ou sem espaços e hífens).</param>
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

    /// <summary>Loja pública: só peças ativas compatíveis com o veículo (RF09 CA3).</summary>
    Task<(IReadOnlyList<StorePartSummary> Items, int Total)> SearchStoreAsync(VehicleFilter vehicle, int page, int pageSize, CancellationToken ct = default);
}
