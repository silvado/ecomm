using Ecommerce.Domain.Catalog;

namespace Ecommerce.Application.Catalog;

public sealed record StockView(int OnHand, int Reserved, int Available);

public sealed record PartSummary(
    Guid Id, string InternalCode, string Title, PartCondition Condition, decimal Price, PartStatus Status,
    StockView Stock, bool HasShippingDimensions, DateTimeOffset UpdatedAt);

public sealed record PartView(
    Guid Id, string InternalCode, string Title, string Description, PartCondition Condition, decimal Price,
    int? LengthCm, int? WidthCm, int? HeightCm, int? WeightG, IReadOnlyList<string> OemCodes,
    PartStatus Status, StockView Stock, bool HasShippingDimensions, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <param name="Search">Trecho do título ou do código interno, ou um código OEM (com ou sem espaços e hífens).</param>
public sealed record PartSearch(string? Search, PartStatus? Status, int Page = 1, int PageSize = 25)
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
