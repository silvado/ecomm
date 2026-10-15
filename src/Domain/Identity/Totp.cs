using System.Security.Cryptography;

namespace Ecommerce.Domain.Identity;

/// <summary>
/// Código de uso único por tempo (TOTP, RFC 6238 sobre HOTP, RFC 4226) para o MFA do superadmin (ADR-0007):
/// HMAC-SHA1, 6 dígitos, passo de 30 s, tolerância de um passo para cada lado.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int SecretSize = 20;
    public static readonly TimeSpan Step = TimeSpan.FromSeconds(30);

    /// <summary>Passos aceitos antes e depois do atual (relógio do celular adiantado ou atrasado).</summary>
    public const int AllowedDrift = 1;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretSize);

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / (long)Step.TotalSeconds;

    /// <summary>HOTP (RFC 4226, seção 5.3) do passo: truncamento dinâmico e módulo 10^<paramref name="digits"/>.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350",
        Justification = "HMAC-SHA1 é o padrão do TOTP (RFC 6238) e o único aceito por todos os apps autenticadores; HMAC-SHA1 segue seguro como PRF.")]
    public static string Compute(ReadOnlySpan<byte> secret, long step, int digits = Digits)
    {
        Span<byte> counter = stackalloc byte[8];
        for (var i = 7; i >= 0; i--)
        {
            counter[i] = (byte)(step & 0xff);
            step >>= 8;
        }

        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(secret, counter, hash);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var modulo = (int)Math.Pow(10, digits);
        return (binary % modulo).ToString(new string('0', digits), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Passo do código, se valer agora e for posterior a <paramref name="lastUsedStep"/>: um código aceito não vale de novo
    /// (quem o viu por cima do ombro não consegue reutilizá-lo).
    /// </summary>
    public static long? Verify(ReadOnlySpan<byte> secret, string? code, DateTimeOffset now, long? lastUsedStep)
    {
        var normalized = Normalize(code);
        if (normalized is null) return null;

        var current = StepAt(now);
        for (var step = current - AllowedDrift; step <= current + AllowedDrift; step++)
        {
            if (step <= lastUsedStep) continue;
            if (CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(Compute(secret, step)), System.Text.Encoding.ASCII.GetBytes(normalized)))
                return step;
        }
        return null;
    }

    /// <summary>Aceita o código digitado com espaços ("123 456"); qualquer outra coisa fora de 6 dígitos é recusada.</summary>
    public static string? Normalize(string? code)
    {
        if (code is null) return null;
        var digits = code.Replace(" ", string.Empty, StringComparison.Ordinal).Trim();
        return digits.Length == Digits && digits.All(char.IsAsciiDigit) ? digits : null;
    }

    /// <summary>URI de cadastro no app autenticador (formato "Key Uri" do Google Authenticator).</summary>
    public static string ProvisioningUri(string issuer, string account, ReadOnlySpan<byte> secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
        $"?secret={Base32.Encode(secret)}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={(int)Step.TotalSeconds}";
}

/// <summary>Base32 (RFC 4648, alfabeto A–Z 2–7, sem preenchimento), como os apps autenticadores esperam o segredo.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var output = new System.Text.StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) output.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return output.ToString();
    }
}
