using Ecommerce.Application.Storage;
using Ecommerce.Domain.Catalog;

namespace Ecommerce.Application.Catalog;

/// <summary>O que a loja pública mostra de uma peça na listagem.</summary>
public sealed record StorePartSummary(Guid Id, string Title, PartCondition Condition, decimal Price, int Available, Guid? CoverPhotoId);

public sealed record StoreCompatibility(string Brand, string Model, string Engine, int YearFrom, int? YearTo);

/// <summary>Página da peça na loja: nada de estoque reservado, custo, medidas internas ou dados do doador (RF10 CA3).</summary>
public sealed record StorePartView(
    Guid Id, string InternalCode, string Title, string Description, PartCondition Condition, decimal Price, int Available,
    IReadOnlyList<Guid> PhotoIds, IReadOnlyList<string> OemCodes, IReadOnlyList<StoreCompatibility> Compatibilities, DateTimeOffset UpdatedAt);

/// <param name="Text">Título (sem diferenciar acentos e maiúsculas), código interno ou OEM (com ou sem espaços e hífens).</param>
public sealed record StoreSearch(string? Text, VehicleFilter? Vehicle, int Page = 1, int PageSize = 24);

public sealed record StorePartPage(IReadOnlyList<StorePartSummary> Items, int Total, int Page, int PageSize);

/// <summary>
/// Catálogo da loja pública do Host (RF13). Só peças ativas; sem estoque, aparecem como indisponíveis ou somem,
/// conforme a configuração da loja (CA4).
/// </summary>
public interface IStoreCatalog
{
    Task<StorePartPage> SearchAsync(StoreSearch search, CancellationToken ct = default);

    Task<StorePartView?> GetAsync(Guid partId, CancellationToken ct = default);

    /// <summary>Uma versão publicada (WebP) de foto de peça ativa da loja; nulo caso contrário.</summary>
    Task<StoredFile?> GetPhotoAsync(Guid photoId, int size, CancellationToken ct = default);
}
