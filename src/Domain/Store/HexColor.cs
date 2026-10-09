using System.Globalization;

namespace Ecommerce.Domain.Store;

/// <summary>Cor no formato <c>#RRGGBB</c> (maiúsculas), com contraste segundo o WCAG 2.x.</summary>
public readonly record struct HexColor
{
    private HexColor(string value) => Value = value;

    public string Value { get; }

    public static HexColor Parse(string? input) =>
        TryParse(input, out var color) ? color : throw new ArgumentException($"Cor inválida: use o formato #RRGGBB.", nameof(input));

    public static bool TryParse(string? input, out HexColor color)
    {
        color = default;
        var value = input?.Trim().ToUpperInvariant();
        if (value is not { Length: 7 } || value[0] != '#' || !value[1..].All(char.IsAsciiHexDigit)) return false;
        color = new HexColor(value);
        return true;
    }

    /// <summary>Luminância relativa (WCAG 2.x, definição de "relative luminance").</summary>
    public double RelativeLuminance()
    {
        static double Channel(string hex)
        {
            var c = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(Value[1..3]) + 0.7152 * Channel(Value[3..5]) + 0.0722 * Channel(Value[5..7]);
    }

    /// <summary>Razão de contraste entre 1:1 e 21:1 (WCAG 2.x, "contrast ratio").</summary>
    public double ContrastWith(HexColor other)
    {
        var (a, b) = (RelativeLuminance(), other.RelativeLuminance());
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>Branco ou preto, o que tiver mais contraste sobre esta cor (texto de botões na cor principal).</summary>
    public HexColor ReadableTextColor() =>
        ContrastWith(White) >= ContrastWith(Black) ? White : Black;

    public static readonly HexColor White = new("#FFFFFF");
    public static readonly HexColor Black = new("#000000");

    public override string ToString() => Value;
}
