namespace Ecommerce.Domain.Orders;

/// <summary>
/// CPF do comprador: 11 dígitos com os 2 verificadores pelo módulo 11 (pesos 10..2 e 11..2).
/// Exigido pela NF-e (RF19) e pela etiqueta do frete.
/// </summary>
public sealed record Cpf
{
    private Cpf(string value) => Value = value;

    /// <summary>Somente os 11 dígitos, sem pontuação.</summary>
    public string Value { get; }

    public static bool TryParse(string? input, out Cpf? cpf)
    {
        cpf = null;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var normalized = new string(input.Where(c => c is not ('.' or '-' or ' ')).ToArray());
        if (normalized.Length != 11 || !normalized.All(char.IsAsciiDigit)) return false;
        if (normalized.All(c => c == normalized[0])) return false;

        if (normalized[9] - '0' != CheckDigit(normalized[..9]) || normalized[10] - '0' != CheckDigit(normalized[..10])) return false;

        cpf = new Cpf(normalized);
        return true;
    }

    public static Cpf Parse(string input) =>
        TryParse(input, out var cpf) ? cpf! : throw new ArgumentException("CPF inválido.", nameof(input));

    public override string ToString() => Value;

    private static int CheckDigit(string body)
    {
        var sum = 0;
        for (var i = 0; i < body.Length; i++) sum += (body[i] - '0') * (body.Length + 1 - i);
        var remainder = sum % 11;
        return remainder < 2 ? 0 : 11 - remainder;
    }
}
