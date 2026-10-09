using Ecommerce.Application.Catalog;
using Ecommerce.Domain.Catalog;
using Ecommerce.Infrastructure.Persistence;

namespace Ecommerce.Infrastructure.Catalog;

/// <summary>Filtros de catálogo usados pelo painel e pela loja.</summary>
internal static class CatalogFilters
{
    /// <summary>
    /// RF09 CA3: peças com alguma compatibilidade no modelo (e na versão, se informada) cujo intervalo efetivo de anos
    /// (o da compatibilidade ou, sem ele, o da versão) contém o ano-modelo pedido.
    /// </summary>
    public static IQueryable<Part> CompatibleWith(TenantDbContext db, IQueryable<Part> parts, VehicleFilter vehicle)
    {
        var (modelId, versionId, year) = (vehicle.ModelId, vehicle.VersionId, vehicle.Year);
        return parts.Where(p => db.PartCompatibilities.Any(c => c.PartId == p.Id && db.VehicleVersions.Any(v =>
            v.Id == c.VehicleVersionId && v.ModelId == modelId && (versionId == null || v.Id == versionId) &&
            (year == null || (year >= (c.YearFrom ?? v.YearFrom) && year <= (c.YearTo ?? v.YearTo ?? int.MaxValue))))));
    }

    /// <summary>Padrão ILIKE "contém", com %, _ e \ do texto tratados como literais.</summary>
    public static string Contains(string text) =>
        "%" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";

    public static string OemOrEmpty(string text)
    {
        try { return PartOemCode.Normalize(text); }
        catch (ArgumentException) { return string.Empty; }
    }
}