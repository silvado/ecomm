using System.Globalization;
using System.Text;

namespace Ecommerce.Application.Catalog;

/// <summary>
/// Comparação de nomes de veículo entre a nossa tabela e a de fora (IA, Mercado Livre): sem acento, sem caixa e por palavras.
/// </summary>
public static class VehicleNames
{
    /// <summary>Minúsculas, sem acento, só letras, dígitos e ponto ("1.0 8V Flex (G5)" → 1.0, 8v, flex, g5).</summary>
    public static List<string> Tokens(string? text)
    {
        var plain = new StringBuilder();
        foreach (var c in (text ?? string.Empty).Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            plain.Append(char.IsLetterOrDigit(c) || c == '.' ? char.ToLowerInvariant(c) : ' ');
        }
        return plain.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim('.')).Where(t => t.Length > 0).ToList();
    }

    /// <summary>Mesmas palavras, na mesma ordem ("Volkswagen" = "VOLKSWAGEN", "Citroën" = "Citroen").</summary>
    public static bool SameName(string? a, string? b) => Tokens(a).SequenceEqual(Tokens(b));

    /// <summary>
    /// A motorização de <paramref name="options"/> que corresponde a <paramref name="engine"/>: a cilindrada ("1.6") precisa
    /// bater e vale a de mais palavras em comum, sem empate — na dúvida, nenhuma (cada lado escreve do seu jeito:
    /// "1.6 8V" no ML, "1.6 8V Flex (G5)" na nossa tabela).
    /// </summary>
    public static T? BestEngine<T>(string engine, IEnumerable<T> options, Func<T, string> name)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(name);
        var ours = Tokens(engine);
        var displacement = ours.FirstOrDefault(t => t.Contains('.', StringComparison.Ordinal));
        if (displacement is null) return null;
        var scored = options
            .Select(o => (Option: o, Tokens: Tokens(name(o))))
            .Where(o => o.Tokens.Contains(displacement))
            .Select(o => (o.Option, Score: o.Tokens.Count(ours.Contains)))
            .OrderByDescending(o => o.Score).ToList();
        if (scored.Count == 0 || (scored.Count > 1 && scored[0].Score == scored[1].Score)) return null;
        return scored[0].Option;
    }
}
