using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Catalog;

/// <summary>
/// Foto da peça (RF08 CA3). Os arquivos ficam no storage: o original no bucket privado e três WebP no público,
/// todos sob <c>{tenantId}/pecas/{partId}/</c>. A posição 0 é a capa.
/// </summary>
public sealed class PartPhoto : ITenantOwned
{
    /// <summary>Lado maior de cada versão publicada: zoom e marketplaces, página da peça, miniatura.</summary>
    public static readonly IReadOnlyList<int> Sizes = [1600, 800, 300];

    public const int MaxUploadBytes = 10 * 1024 * 1024;

    /// <summary>Limite de pixels da imagem enviada: protege contra "bombas" pequenas no disco e enormes na memória.</summary>
    public const long MaxPixels = 40_000_000;

    private PartPhoto() { }

    internal PartPhoto(Guid tenantId, Guid partId, Guid id, int position, string originalContentType, DateTimeOffset now)
    {
        Id = id;
        TenantId = tenantId;
        PartId = partId;
        Position = position;
        OriginalContentType = originalContentType;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid PartId { get; private set; }
    public int Position { get; internal set; }
    public string OriginalContentType { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }

    public static string PublicKey(Guid tenantId, Guid partId, Guid photoId, int size)
    {
        if (!Sizes.Contains(size)) throw new ArgumentOutOfRangeException(nameof(size), "Tamanho de foto inexistente.");
        return $"{tenantId}/pecas/{partId}/{photoId}-{size}.webp";
    }

    public static string OriginalKey(Guid tenantId, Guid partId, Guid photoId) => $"{tenantId}/pecas/{partId}/{photoId}-original";
}
