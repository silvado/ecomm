using System.Security.Cryptography;
using System.Text;

namespace Ecommerce.Infrastructure.Vault;

/// <summary>
/// AES-256-GCM. Formato: nonce (12) | tag (16) | texto cifrado. O "contexto" (AAD) amarra a cifra ao dono:
/// um texto cifrado copiado para outro tenant ou outro segredo não decifra.
/// </summary>
internal static class EnvelopeCrypto
{
    public const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeySize);

    public static byte[] Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext, string context)
    {
        var output = new byte[NonceSize + TagSize + plaintext.Length];
        var nonce = output.AsSpan(0, NonceSize);
        var tag = output.AsSpan(NonceSize, TagSize);
        var ciphertext = output.AsSpan(NonceSize + TagSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(context));
        return output;
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> payload, string context)
    {
        if (payload.Length < NonceSize + TagSize) throw new CryptographicException("Texto cifrado inválido.");
        var plaintext = new byte[payload.Length - NonceSize - TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(payload[..NonceSize], payload[(NonceSize + TagSize)..], payload.Slice(NonceSize, TagSize), plaintext,
            Encoding.UTF8.GetBytes(context));
        return plaintext;
    }
}
