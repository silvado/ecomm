namespace Ecommerce.Domain.Common;

/// <summary>
/// Reconhece PNG, JPEG e WebP pelos primeiros bytes (assinatura do formato), nunca pela extensão ou pelo
/// Content-Type enviado: um HTML renomeado para .png é recusado.
/// </summary>
public static class ImageSignature
{
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
}
