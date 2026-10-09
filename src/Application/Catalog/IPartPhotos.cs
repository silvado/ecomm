using Ecommerce.Application.Storage;

namespace Ecommerce.Application.Catalog;

public enum PhotoOutcome
{
    Done,
    NotFound,
    /// <summary>Regra da peça impediu (limite de 20, última foto de peça ativa).</summary>
    Conflict,
    Invalid,
}

/// <summary><see cref="Message"/> vem pronta para o usuário quando o resultado não é <see cref="PhotoOutcome.Done"/>.</summary>
public sealed record PhotoResult(PhotoOutcome Outcome, Guid? PhotoId = null, string? Message = null);

/// <summary>Fotos das peças da loja do escopo (RF08 CA1, CA3).</summary>
public interface IPartPhotos
{
    /// <summary>JPEG, PNG ou WebP (pelo conteúdo) até 10 MB e 40 MP; vira WebP em 1600, 800 e 300 px.</summary>
    Task<PhotoResult> AddAsync(Guid partId, Stream content, CancellationToken ct = default);

    Task<PhotoResult> RemoveAsync(Guid partId, Guid photoId, CancellationToken ct = default);

    Task<PhotoResult> ReorderAsync(Guid partId, IReadOnlyList<Guid> photoIds, CancellationToken ct = default);

    /// <summary>Uma das versões publicadas (WebP). Nulo se a foto não for desta peça e desta loja.</summary>
    Task<StoredFile?> GetAsync(Guid partId, Guid photoId, int size, CancellationToken ct = default);
}
