using System.Globalization;
using Ecommerce.Application.Catalog;

namespace Ecommerce.Application.Channels;

/// <summary>Veículo compatível informado num anúncio do canal: marca, modelo, ano e, quando o canal diz, a motorização.</summary>
public sealed record ChannelVehicle(string Brand, string Model, int Year, string? Engine)
{
    public string Label => $"{Brand} {Model} {Year.ToString(CultureInfo.InvariantCulture)}{(Engine is null ? string.Empty : " " + Engine)}";
}

/// <summary>Uma versão da nossa tabela de veículos (não descontinuada), com marca e modelo.</summary>
public sealed record VehicleCandidate(Guid VersionId, Guid ModelId, string Brand, string Model, string Engine, int YearFrom, int? YearTo);

/// <summary>Compatibilidade a gravar na peça: versão e faixa de anos contínua.</summary>
public sealed record CompatibilityMatch(Guid VersionId, int YearFrom, int YearTo);

/// <summary>O que casou com a tabela e o que ficou de fora (para o relatório).</summary>
public sealed record CompatibilityMatchResult(IReadOnlyList<CompatibilityMatch> Matches, IReadOnlyList<string> Unmatched);

/// <summary>
/// Regras fixas de como os veículos de um anúncio do canal viram compatibilidades da peça (RF11 CA2). Nunca se grava
/// veículo a mais: marca e modelo precisam bater pelo nome (o modelo do canal pode ser o começo do nosso: "Gol" → "Gol G5"),
/// a versão precisa cobrir o ano e, com motorização informada, ela precisa corresponder a uma só das nossas; sem
/// motorização, só vale se o modelo tiver uma única versão naquele ano. O que não casa vai para a lista "não encontrados".
/// </summary>
public static class ChannelCompatibilityRules
{
    public static CompatibilityMatchResult Match(IEnumerable<ChannelVehicle> vehicles, IReadOnlyList<VehicleCandidate> table)
    {
        ArgumentNullException.ThrowIfNull(vehicles);
        ArgumentNullException.ThrowIfNull(table);
        var years = new Dictionary<Guid, SortedSet<int>>();
        var unmatched = new List<string>();
        foreach (var vehicle in vehicles.Distinct())
        {
            if (Version(vehicle, table) is { } versionId)
            {
                if (!years.TryGetValue(versionId, out var set)) years[versionId] = set = [];
                set.Add(vehicle.Year);
            }
            else if (!unmatched.Contains(vehicle.Label, StringComparer.Ordinal)) unmatched.Add(vehicle.Label);
        }

        var matches = new List<CompatibilityMatch>();
        foreach (var (versionId, set) in years)
        {
            int? from = null, to = null;
            foreach (var year in set)
            {
                if (from is not null && year == to + 1)
                {
                    to = year;
                    continue;
                }
                if (from is not null) matches.Add(new CompatibilityMatch(versionId, from.Value, to!.Value));
                (from, to) = (year, year);
            }
            if (from is not null) matches.Add(new CompatibilityMatch(versionId, from.Value, to!.Value));
        }
        return new CompatibilityMatchResult(matches, unmatched);
    }

    private static Guid? Version(ChannelVehicle vehicle, IReadOnlyList<VehicleCandidate> table)
    {
        var brand = table.Where(c => VehicleNames.SameName(c.Brand, vehicle.Brand)).ToList();
        var model = VehicleNames.Tokens(vehicle.Model);
        if (model.Count == 0) return null;
        var models = brand.Where(c => VehicleNames.Tokens(c.Model).SequenceEqual(model)).ToList();
        if (models.Count == 0) models = brand.Where(c => VehicleNames.Tokens(c.Model) is var ours && ours.Count > model.Count && ours.Take(model.Count).SequenceEqual(model)).ToList();
        var covering = models.Where(c => c.YearFrom <= vehicle.Year && (c.YearTo is null || c.YearTo >= vehicle.Year)).ToList();
        if (covering.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(vehicle.Engine)) return covering.Count == 1 ? covering[0].VersionId : null;
        return VehicleNames.BestEngine(vehicle.Engine, covering, c => c.Engine)?.VersionId;
    }
}
