namespace Ecommerce.Domain.Platform;

/// <summary>
/// CNPJ numérico ou alfanumérico (IN RFB 2.229/2024): 12 posições [A-Z0-9] + 2 dígitos verificadores numéricos.
/// Cálculo do DV: módulo 11 com pesos 2..9, cada caractere valendo (código ASCII − 48) — o que mantém o resultado
/// idêntico ao do CNPJ só com números.
/// </summary>
public sealed record Cnpj
{
    private Cnpj(string value) => Value = value;

    /// <summary>Somente os 14 caracteres, maiúsculos, sem pontuação.</summary>
    public string Value { get; }

    public static bool TryParse(string? input, out Cnpj? cnpj)
    {
        cnpj = null;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var normalized = new string(input.Where(c => c is not ('.' or '/' or '-' or ' ')).ToArray()).ToUpperInvariant();
        if (normalized.Length != 14) return false;
        if (!normalized[..12].All(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z')) return false;
        if (!normalized[12..].All(char.IsAsciiDigit)) return false;
        if (normalized.All(c => c == normalized[0])) return false;

        var first = CheckDigit(normalized[..12]);
        var second = CheckDigit(normalized[..12] + first);
        if (normalized[12] - '0' != first || normalized[13] - '0' != second) return false;

        cnpj = new Cnpj(normalized);
        return true;
    }

    public static Cnpj Parse(string input) =>
        TryParse(input, out var cnpj) ? cnpj! : throw new ArgumentException("CNPJ inválido.", nameof(input));

    public override string ToString() => Value;

    private static int CheckDigit(string body)
    {
        var sum = 0;
        var weight = 2;
        for (var i = body.Length - 1; i >= 0; i--)
        {
            sum += (body[i] - '0') * weight;
            weight = weight == 9 ? 2 : weight + 1;
        }
        var remainder = sum % 11;
        return remainder < 2 ? 0 : 11 - remainder;
    }
}
