using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Store;

/// <summary>
/// Logo da loja (RF01 CA2): PNG, JPEG ou WebP reconhecidos pelo conteúdo (<see cref="ImageSignature"/>), até 2 MB.
/// HIPÓTESE Q21: SVG fica para depois.
/// </summary>
public static class LogoImage
{
    public const int MaxBytes = 2 * 1024 * 1024;

    public const int HeaderLength = ImageSignature.HeaderLength;

    public static IReadOnlyList<string> AcceptedTypes => ImageSignature.AcceptedTypes;

    public static string? DetectContentType(ReadOnlySpan<byte> header) => ImageSignature.DetectContentType(header);

    /// <summary>Chave no storage: sempre sob o tenant, com id novo a cada troca (URL imutável, cache longo).</summary>
    public static string StorageKey(Guid tenantId, Guid logoId) => $"{tenantId}/marca/logo-{logoId}";
}
