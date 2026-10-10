namespace Ecommerce.Domain.Shipping;

/// <summary>CEP: 8 dígitos, guardado sem pontuação ("01310-100" → "01310100").</summary>
public readonly record struct PostalCode
{
    private PostalCode(string value) => Value = value;

    public string Value { get; }

    public static bool TryParse(string? input, out PostalCode postalCode)
    {
        postalCode = default;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var trimmed = input.Trim();
        if (trimmed.Any(c => !(char.IsAsciiDigit(c) || c is '-' or '.' or ' '))) return false;
        var digits = new string(trimmed.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length != 8 || digits == "00000000") return false;
        postalCode = new PostalCode(digits);
        return true;
    }

    public static PostalCode Parse(string? input) =>
        TryParse(input, out var postalCode) ? postalCode : throw new ArgumentException("CEP inválido: informe os 8 dígitos.", nameof(input));

    /// <summary>"01310-100".</summary>
    public string Formatted => $"{Value[..5]}-{Value[5..]}";

    public override string ToString() => Value;
}
