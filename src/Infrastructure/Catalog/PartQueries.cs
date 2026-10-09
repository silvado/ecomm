using Ecommerce.Application.Catalog;
using Ecommerce.Domain.Catalog;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Catalog;

/// <summary>Leituras do catálogo do tenant do escopo. Sem tenant no escopo, o RLS não devolve nada (falha fechada).</summary>
public sealed class PartQueries(TenantDbContext db) : IPartQueries
{
    public async Task<PartPage> SearchAsync(PartSearch search, CancellationToken ct = default)
    {
        var page = Math.Max(1, search.Page);
        var size = Math.Clamp(search.PageSize, 1, PartSearch.MaxPageSize);

        var parts = db.Parts.AsNoTracking();
        if (search.Status is { } status) parts = parts.Where(p => p.Status == status);
        if (search.Vehicle is { } vehicle) parts = CompatibleWith(parts, vehicle);
        if (!string.IsNullOrWhiteSpace(search.Search))
        {
            var text = search.Search.Trim();
            var like = $"%{EscapeLike(text)}%";
            var oem = OemOrEmpty(text);
            parts = parts.Where(p => EF.Functions.ILike(p.Title, like, "\\")
                || EF.Functions.ILike(p.InternalCode, like, "\\")
                || (oem != "" && p.OemCodes.Any(c => c.Code == oem)));
        }

        var total = await parts.CountAsync(ct);
        var items = await (from p in parts
                           join s in db.Stocks on p.Id equals s.PartId
                           orderby p.UpdatedAt descending, p.Id
                           select new PartSummary(p.Id, p.InternalCode, p.Title, p.Condition, p.Price, p.Status,
                               new StockView(s.OnHand, s.Reserved, s.OnHand - s.Reserved),
                               p.LengthCm != null && p.WidthCm != null && p.HeightCm != null && p.WeightG != null,
                               p.UpdatedAt,
                               db.PartPhotos.Where(f => f.PartId == p.Id && f.Position == 0).Select(f => (Guid?)f.Id).FirstOrDefault()))
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PartPage(items, total, page, size);
    }

    public async Task<(IReadOnlyList<StorePartSummary> Items, int Total)> SearchStoreAsync(
        VehicleFilter vehicle, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, PartSearch.MaxPageSize);
        var parts = CompatibleWith(db.Parts.AsNoTracking().Where(p => p.Status == PartStatus.Active), vehicle);

        var total = await parts.CountAsync(ct);
        var items = await (from p in parts
                           join s in db.Stocks on p.Id equals s.PartId
                           orderby p.Title, p.Id
                           select new StorePartSummary(p.Id, p.Title, p.Condition, p.Price, s.OnHand - s.Reserved,
                               db.PartPhotos.Where(f => f.PartId == p.Id && f.Position == 0).Select(f => (Guid?)f.Id).FirstOrDefault()))
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    public async Task<PartView?> GetAsync(Guid partId, CancellationToken ct = default)
    {
        var part = await db.Parts.AsNoTracking().Include(p => p.OemCodes).Include(p => p.Photos).SingleOrDefaultAsync(p => p.Id == partId, ct);
        if (part is null) return null;
        var stock = await db.Stocks.AsNoTracking().Where(s => s.PartId == partId)
            .Select(s => new StockView(s.OnHand, s.Reserved, s.OnHand - s.Reserved)).SingleAsync(ct);
        var compatibilities = await (from c in db.PartCompatibilities
                                     join v in db.VehicleVersions on c.VehicleVersionId equals v.Id
                                     join m in db.VehicleModels on v.ModelId equals m.Id
                                     join b in db.VehicleBrands on m.BrandId equals b.Id
                                     where c.PartId == partId
                                     orderby b.Name, m.Name, v.YearFrom, v.Engine
                                     select new CompatibilityView(c.Id, v.Id, b.Name, m.Name, v.Engine,
                                         c.YearFrom ?? v.YearFrom, c.YearFrom != null ? c.YearTo : v.YearTo, c.YearFrom != null, v.Discontinued))
            .AsNoTracking().ToListAsync(ct);
        return new PartView(part.Id, part.InternalCode, part.Title, part.Description, part.Condition, part.Price,
            part.LengthCm, part.WidthCm, part.HeightCm, part.WeightG, part.OemCodes.Select(c => c.Code).Order().ToList(),
            part.Status, stock, part.HasShippingDimensions, part.CreatedAt, part.UpdatedAt,
            part.Photos.Select(f => new PhotoView(f.Id, f.Position)).ToList(), compatibilities);
    }

    /// <summary>
    /// RF09 CA3: peças com alguma compatibilidade no modelo (e na versão, se informada) cujo intervalo efetivo de anos
    /// (o da compatibilidade ou, sem ele, o da versão) contém o ano-modelo pedido.
    /// </summary>
    private IQueryable<Part> CompatibleWith(IQueryable<Part> parts, VehicleFilter vehicle)
    {
        var (modelId, versionId, year) = (vehicle.ModelId, vehicle.VersionId, vehicle.Year);
        return parts.Where(p => db.PartCompatibilities.Any(c => c.PartId == p.Id && db.VehicleVersions.Any(v =>
            v.Id == c.VehicleVersionId && v.ModelId == modelId && (versionId == null || v.Id == versionId) &&
            (year == null || (year >= (c.YearFrom ?? v.YearFrom) && year <= (c.YearTo ?? v.YearTo ?? int.MaxValue))))));
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static string OemOrEmpty(string text)
    {
        try { return PartOemCode.Normalize(text); }
        catch (ArgumentException) { return string.Empty; }
    }
}
