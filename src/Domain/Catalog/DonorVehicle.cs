using System.Text.RegularExpressions;
using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Catalog;

/// <summary>Dados do veículo doador como o usuário informa (texto livre; a entidade normaliza e valida).</summary>
public sealed record DonorVehicleDetails(
    string? Plate, string? Chassis, string? Renavam, string? Brand, string? Model, int? ModelYear, string? Color,
    string? DeregistrationCertificate, string? EntryInvoiceKey, DateOnly? EntryDate, string? Notes);

/// <summary>
/// Veículo de onde saem as peças usadas (RF10, Lei 12.977/2014): cadastrado uma vez, com a NF de entrada e a certidão de
/// baixa, e ligado às peças. Dados internos da loja: nunca aparecem na vitrine nem nos canais (RF10 CA3).
/// </summary>
public sealed partial class DonorVehicle : ITenantOwned
{
    public const int TextMaxLength = 60;
    public const int ColorMaxLength = 30;
    public const int CertificateMaxLength = 40;
    public const int NotesMaxLength = 1000;

    private DonorVehicle() { }

    public static DonorVehicle Create(Guid tenantId, DonorVehicleDetails details, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant obrigatório.", nameof(tenantId));
        var vehicle = new DonorVehicle { Id = Guid.CreateVersion7(), TenantId = tenantId, CreatedAt = now };
        vehicle.Update(details, now);
        return vehicle;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>Placa antiga (ABC1234) ou Mercosul (ABC1D23), maiúscula e sem hífen.</summary>
    public string? Plate { get; private set; }

    /// <summary>Chassi (VIN), 17 caracteres.</summary>
    public string? Chassis { get; private set; }

    /// <summary>11 dígitos, com o dígito verificador conferido.</summary>
    public string? Renavam { get; private set; }

    public string Brand { get; private set; } = string.Empty;
    public string Model { get; private set; } = string.Empty;
    public int? ModelYear { get; private set; }
    public string? Color { get; private set; }

    /// <summary>Número da certidão de baixa do veículo (Detran).</summary>
    public string? DeregistrationCertificate { get; private set; }

    /// <summary>Chave de acesso (44 dígitos) da NF de entrada do veículo no desmanche (Lei 12.977, art. 7º).</summary>
    public string? EntryInvoiceKey { get; private set; }

    public DateOnly? EntryDate { get; private set; }
    public string? Notes { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Tem tudo o que o modo desmontadora exige para as peças dele serem publicadas.</summary>
    public bool IsCompleteForDismantler => MissingForDismantler().Count == 0;

    public IReadOnlyList<string> MissingForDismantler()
    {
        var missing = new List<string>();
        if (Chassis is null) missing.Add("chassi");
        if (Renavam is null) missing.Add("Renavam");
        if (DeregistrationCertificate is null) missing.Add("certidão de baixa");
        if (EntryInvoiceKey is null) missing.Add("NF de entrada");
        return missing;
    }

    /// <summary>"VW Gol 2011 · ABC1D23" — como o veículo aparece nas listas do painel.</summary>
    public string Label => string.Join(" · ", new[] { $"{Brand} {Model}{(ModelYear is { } y ? $" {y}" : "")}", Plate ?? Chassis }.Where(s => !string.IsNullOrEmpty(s)));

    public void Update(DonorVehicleDetails details, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(details);
        var brand = Text(details.Brand, TextMaxLength, "Marca") ?? throw new ArgumentException("Informe a marca do veículo.", nameof(details));
        var model = Text(details.Model, TextMaxLength, "Modelo") ?? throw new ArgumentException("Informe o modelo do veículo.", nameof(details));
        var plate = NormalizePlate(details.Plate);
        var chassis = NormalizeChassis(details.Chassis);
        if (plate is null && chassis is null) throw new ArgumentException("Informe a placa ou o chassi do veículo.", nameof(details));
        var renavam = NormalizeRenavam(details.Renavam);
        var invoice = NormalizeInvoiceKey(details.EntryInvoiceKey);
        if (details.ModelYear is { } year && (year < 1900 || year > now.Year + 1))
            throw new ArgumentException($"Ano-modelo entre 1900 e {now.Year + 1}.", nameof(details));
        if (details.EntryDate is { } entry && entry > DateOnly.FromDateTime(now.UtcDateTime).AddDays(1))
            throw new ArgumentException("A data de entrada não pode ser futura.", nameof(details));

        (Plate, Chassis, Renavam, Brand, Model, ModelYear) = (plate, chassis, renavam, brand, model, details.ModelYear);
        Color = Text(details.Color, ColorMaxLength, "Cor");
        DeregistrationCertificate = Text(details.DeregistrationCertificate, CertificateMaxLength, "Certidão de baixa");
        EntryInvoiceKey = invoice;
        EntryDate = details.EntryDate;
        Notes = Text(details.Notes, NotesMaxLength, "Observações", multiline: true);
        UpdatedAt = now;
    }

    [GeneratedRegex("^[A-Z]{3}[0-9][A-Z0-9][0-9]{2}$")]
    private static partial Regex PlateFormat();

    // VIN: 17 caracteres, sem I, O e Q (ISO 3779).
    [GeneratedRegex("^[A-HJ-NPR-Z0-9]{17}$")]
    private static partial Regex ChassisFormat();

    public static string? NormalizePlate(string? value)
    {
        var plate = Compact(value);
        if (plate is null) return null;
        return PlateFormat().IsMatch(plate) ? plate : throw new ArgumentException("Placa inválida: use ABC1234 ou ABC1D23.", nameof(value));
    }

    public static string? NormalizeChassis(string? value)
    {
        var chassis = Compact(value);
        if (chassis is null) return null;
        return ChassisFormat().IsMatch(chassis) ? chassis : throw new ArgumentException("Chassi inválido: 17 letras e números (sem I, O e Q).", nameof(value));
    }

    /// <summary>Renavam com 9 ou 11 dígitos (o de 9 ganha zeros à esquerda); DV: módulo 11 com pesos 3,2,9,8,7,6,5,4,3,2.</summary>
    public static string? NormalizeRenavam(string? value)
    {
        var digits = Compact(value);
        if (digits is null) return null;
        if (!digits.All(char.IsAsciiDigit) || digits.Length is not (9 or 11)) throw new ArgumentException("Renavam inválido: 11 dígitos.", nameof(value));
        digits = digits.PadLeft(11, '0');
        int[] weights = [3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
        var sum = weights.Select((w, i) => w * (digits[i] - '0')).Sum();
        var check = sum * 10 % 11 % 10;
        return digits[10] - '0' == check ? digits : throw new ArgumentException("Renavam inválido: confira os dígitos.", nameof(value));
    }

    /// <summary>Chave de acesso da NF-e: 44 dígitos, DV módulo 11 com pesos 2 a 9 da direita para a esquerda.</summary>
    public static string? NormalizeInvoiceKey(string? value)
    {
        var digits = Compact(value);
        if (digits is null) return null;
        if (digits.Length != 44 || !digits.All(char.IsAsciiDigit)) throw new ArgumentException("Chave da NF de entrada inválida: 44 dígitos.", nameof(value));
        var sum = 0;
        var weight = 2;
        for (var i = 42; i >= 0; i--)
        {
            sum += (digits[i] - '0') * weight;
            weight = weight == 9 ? 2 : weight + 1;
        }
        var remainder = sum % 11;
        var check = remainder < 2 ? 0 : 11 - remainder;
        return digits[43] - '0' == check ? digits : throw new ArgumentException("Chave da NF de entrada inválida: confira os dígitos.", nameof(value));
    }

    private static string? Compact(string? value)
    {
        var text = new string((value ?? string.Empty).Where(c => c is not (' ' or '-' or '.' or '/')).ToArray()).ToUpperInvariant();
        return text.Length == 0 ? null : text;
    }

    private static string? Text(string? value, int max, string name, bool multiline = false)
    {
        var text = multiline
            ? (value ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim()
            : string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length > max) throw new ArgumentException($"{name} com no máximo {max} caracteres.", nameof(value));
        return text.Length == 0 ? null : text;
    }
}
