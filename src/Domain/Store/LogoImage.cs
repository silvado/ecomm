namespace Ecommerce.Domain.Store;

/// <summary>
/// Logo da loja (RF01 CA2). O tipo vem dos primeiros bytes (assinatura do formato), nunca da extensão ou do
/// Content-Type enviado: um HTML renomeado para .png é recusado. HIPÓTESE Q21: SVG fica para depois.
/// </summary>
public static class LogoImage
{
    public const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>Bytes necessários para reconhecer qualquer dos formatos aceitos.</summary>
    public const int HeaderLength = 12;

    public static readonly IReadOnlyList<string> AcceptedTypes = ["image/png", "image/jpeg", "image/webp"];

    /// <summary>Content-Type do formato reconhecido, ou nulo se não for PNG, JPEG ou WebP.</summary>
    public static string? DetectContentType(ReadOnlySpan<byte> header)
    {
        // PNG: 89 50 4E 47 0D 0A 1A 0A (ISO/IEC 15948, seção 5.2)
        if (header.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        // JPEG: FF D8 FF (marcador SOI seguido do primeiro marcador)
        if (header.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })) return "image/jpeg";
        // WebP: "RIFF" <tamanho de 4 bytes> "WEBP" (RFC 9649, seção 2.5)
        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }

    /// <summary>Chave no storage: sempre sob o tenant, com id novo a cada troca (URL imutável, cache longo).</summary>
    public static string StorageKey(Guid tenantId, Guid logoId) => $"{tenantId}/marca/logo-{logoId}";
}
