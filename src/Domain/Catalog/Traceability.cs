namespace Ecommerce.Domain.Catalog;

/// <summary>Campo extra de rastreabilidade configurado pela loja (RF10 CA1), por exemplo "Nº do registro no Detran".</summary>
public sealed record TraceabilityField(string Key, string Label, bool Required);

/// <summary>
/// Regras de rastreabilidade (RF10). No modo desmontadora (Lei 12.977, CA2) uma peça <b>usada</b> só vai à venda com o
/// veículo doador completo, a etiqueta de rastreabilidade e os campos extras obrigatórios; fora dele, tudo é opcional.
/// </summary>
public static class Traceability
{
    public const int MaxFields = 10;
    public const int LabelMaxLength = 40;

    /// <summary>O que falta para a peça poder ficar ativa (vazio = pode).</summary>
    public static IReadOnlyList<string> MissingForSale(bool dismantlerMode, IReadOnlyList<TraceabilityField> fields, Part part, DonorVehicle? donor)
    {
        if (!dismantlerMode || part.Condition != PartCondition.Used) return [];
        var missing = new List<string>();
        if (donor is null) missing.Add("veículo doador");
        else if (donor.MissingForDismantler() is { Count: > 0 } donorMissing) missing.Add($"dados do veículo doador ({string.Join(", ", donorMissing)})");
        if (part.TraceabilityTag is null) missing.Add("etiqueta de rastreabilidade");
        missing.AddRange(fields.Where(f => f.Required && !part.CustomFields.ContainsKey(f.Key)).Select(f => f.Label));
        return missing;
    }

    public static string Message(IReadOnlyList<string> missing) =>
        $"Modo desmontadora (Lei 12.977): preencha antes de pôr à venda — {string.Join("; ", missing)}.";

    /// <summary>
    /// Nova lista de campos: rótulos obrigatórios e únicos; campos sem chave (novos) ganham uma; chaves de campos que
    /// continuam mantêm-se (os valores já preenchidos nas peças não se perdem).
    /// </summary>
    public static IReadOnlyList<TraceabilityField> NormalizeFields(IReadOnlyList<TraceabilityField> fields)
    {
        if (fields.Count > MaxFields) throw new ArgumentException($"No máximo {MaxFields} campos extras.", nameof(fields));
        var result = new List<TraceabilityField>();
        foreach (var field in fields)
        {
            var label = string.Join(' ', (field.Label ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (label.Length is 0 or > LabelMaxLength) throw new ArgumentException($"Nome do campo obrigatório, até {LabelMaxLength} caracteres.", nameof(fields));
            if (result.Any(r => string.Equals(r.Label, label, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException($"Campo \"{label}\" repetido.", nameof(fields));
            var key = string.IsNullOrWhiteSpace(field.Key) ? Guid.CreateVersion7().ToString("N")[^12..] : field.Key.Trim();
            if (key.Length > 40 || !key.All(char.IsAsciiLetterOrDigit) || result.Any(r => r.Key == key)) throw new ArgumentException("Chave de campo inválida.", nameof(fields));
            result.Add(new TraceabilityField(key, label, field.Required));
        }
        return result;
    }
}
