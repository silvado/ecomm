namespace Ecommerce.Domain.Vehicles;

/// <summary>
/// Tabela de veículos da <b>plataforma</b> (RF09 CA2): compartilhada entre as lojas, sem tenant. Fonte no banco
/// <c>plataforma</c>; réplica somente leitura, com os mesmos ids, no schema <c>ref</c> de cada banco de lojas.
/// </summary>
public sealed class VehicleBrand
{
    public const int NameMaxLength = 60;

    private VehicleBrand() { }

    public VehicleBrand(Guid id, string name)
    {
        Id = id;
        Name = VehicleText.Name(name, NameMaxLength, "Marca");
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
}

public sealed class VehicleModel
{
    public const int NameMaxLength = 80;

    private VehicleModel() { }

    public VehicleModel(Guid id, Guid brandId, string name)
    {
        Id = id;
        BrandId = brandId;
        Name = VehicleText.Name(name, NameMaxLength, "Modelo");
    }

    public Guid Id { get; private set; }
    public Guid BrandId { get; private set; }
    public string Name { get; private set; } = string.Empty;
}

/// <summary>
/// Versão = modelo + motorização + faixa de anos (ex.: Gol G5 · 1.0 8V Flex · 2009–2012). <see cref="YearTo"/> nulo =
/// ainda em produção. Versões nunca são apagadas pela importação: quem sai da planilha vira <see cref="Discontinued"/>
/// (some da seleção de novas compatibilidades, mas as existentes continuam valendo).
/// </summary>
public sealed class VehicleVersion
{
    public const int EngineMaxLength = 80;
    public const int MinYear = 1950;

    private VehicleVersion() { }

    public VehicleVersion(Guid id, Guid modelId, string engine, int yearFrom, int? yearTo, int currentYear)
    {
        Id = id;
        ModelId = modelId;
        Engine = VehicleText.Name(engine, EngineMaxLength, "Motorização");
        SetYears(yearFrom, yearTo, currentYear);
    }

    public Guid Id { get; private set; }
    public Guid ModelId { get; private set; }
    public string Engine { get; private set; } = string.Empty;
    public int YearFrom { get; private set; }
    public int? YearTo { get; private set; }

    /// <summary>Código equivalente na árvore de veículos do Mercado Livre (E2).</summary>
    public string? MlReference { get; private set; }

    public bool Discontinued { get; private set; }

    /// <summary>Planilha nova pode fechar a faixa (fim de produção) ou corrigir o ano final.</summary>
    public void ChangeYearTo(int? yearTo, int currentYear) => SetYears(YearFrom, yearTo, currentYear);

    public void Discontinue() => Discontinued = true;

    public void Restore() => Discontinued = false;

    /// <summary>O ano-modelo informado está na faixa desta versão.</summary>
    public bool Covers(int year) => year >= YearFrom && (YearTo is null || year <= YearTo);

    public static void ValidateYears(int yearFrom, int? yearTo, int currentYear)
    {
        // Ano-modelo pode ser o seguinte ao calendário (carro 2027 vendido em 2026).
        if (yearFrom < MinYear || yearFrom > currentYear + 1) throw new ArgumentException($"Ano inicial precisa estar entre {MinYear} e {currentYear + 1}.", nameof(yearFrom));
        if (yearTo is not null && (yearTo < yearFrom || yearTo > currentYear + 1))
            throw new ArgumentException($"Ano final precisa estar entre o ano inicial e {currentYear + 1}.", nameof(yearTo));
    }

    private void SetYears(int yearFrom, int? yearTo, int currentYear)
    {
        ValidateYears(yearFrom, yearTo, currentYear);
        YearFrom = yearFrom;
        YearTo = yearTo;
    }
}

internal static class VehicleText
{
    public static string Name(string? value, int maxLength, string field)
    {
        var text = string.Join(' ', (value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (text.Length is 0 || text.Length > maxLength) throw new ArgumentException($"{field} obrigatória, até {maxLength} caracteres.", nameof(value));
        return text;
    }
}
