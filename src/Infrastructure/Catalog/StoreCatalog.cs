using Ecommerce.Application.Catalog;
using Ecommerce.Application.Storage;
using Ecommerce.Domain.Catalog;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Catalog;

/// <summary>
/// Catálogo da loja pública (RF13), sempre no tenant do Host (RLS + filtro global). Só peças ativas; as sem estoque
/// aparecem como indisponíveis ou somem, conforme a loja (CA4). Busca por texto sem diferenciar acentos (unaccent).
/// </summary>
public sealed class StoreCatalog(TenantDbContext db, IFileStorage storage) : IStoreCatalog
{
    public async Task<StorePartPage> SearchAsync(StoreSearch search, CancellationToken ct = default)
    {
        var page = Math.Max(1, search.Page);
        var size = Math.Clamp(search.PageSize, 1, PartSearch.MaxPageSize);

        var parts = await VisibleAsync(ct);
        if (search.Vehicle is { } vehicle) parts = CatalogFilters.CompatibleWith(db, parts, vehicle);
        if (!string.IsNullOrWhiteSpace(search.Text))
        {
            var text = search.Text.Trim();
            var like = CatalogFilters.Contains(text);
            var oem = CatalogFilters.OemOrEmpty(text);
            parts = parts.Where(p => EF.Functions.ILike(EF.Functions.Unaccent(p.Title), EF.Functions.Unaccent(like), "\\")
                || EF.Functions.ILike(p.InternalCode, like, "\\")
                || (oem != "" && p.OemCodes.Any(c => c.Code == oem)));
        }

        var total = await parts.CountAsync(ct);
        var items = await (from p in parts
                           join s in db.Stocks on p.Id equals s.PartId
                           orderby p.UpdatedAt descending, p.Id
                           select new StorePartSummary(p.Id, p.Title, p.Condition, p.Price, s.OnHand - s.Reserved,
                               db.PartPhotos.Where(f => f.PartId == p.Id && f.Position == 0).Select(f => (Guid?)f.Id).FirstOrDefault()))
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new StorePartPage(items, total, page, size);
    }

    public async Task<StorePartView?> GetAsync(Guid partId, CancellationToken ct = default)
    {
        var part = await (await VisibleAsync(ct)).Include(p => p.OemCodes).Include(p => p.Photos).SingleOrDefaultAsync(p => p.Id == partId, ct);
        if (part is null) return null;
        var available = await db.Stocks.Where(s => s.PartId == partId).Select(s => s.OnHand - s.Reserved).SingleAsync(ct);
        var compatibilities = await (from c in db.PartCompatibilities
                                     join v in db.VehicleVersions on c.VehicleVersionId equals v.Id
                                     join m in db.VehicleModels on v.ModelId equals m.Id
                                     join b in db.VehicleBrands on m.BrandId equals b.Id
                                     where c.PartId == partId
                                     orderby b.Name, m.Name, v.YearFrom, v.Engine
                                     select new StoreCompatibility(b.Name, m.Name, v.Engine,
                                         c.YearFrom ?? v.YearFrom, c.YearFrom != null ? c.YearTo : v.YearTo))
            .AsNoTracking().ToListAsync(ct);
        return new StorePartView(part.Id, part.InternalCode, part.Title, part.Description, part.Condition, part.Price, available,
            part.Photos.Select(f => f.Id).ToList(), part.OemCodes.Select(c => c.Code).Order().ToList(), compatibilities, part.UpdatedAt);
    }

    public async Task<StoredFile?> GetPhotoAsync(Guid photoId, int size, CancellationToken ct = default)
    {
        if (!PartPhoto.Sizes.Contains(size)) return null;
        var visible = await VisibleAsync(ct);
        var photo = await db.PartPhotos.AsNoTracking().Where(f => f.Id == photoId && visible.Any(p => p.Id == f.PartId))
            .Select(f => new { f.TenantId, f.PartId }).SingleOrDefaultAsync(ct);
        return photo is null ? null : await storage.GetAsync(StorageArea.Public, PartPhoto.PublicKey(photo.TenantId, photo.PartId, photoId, size), ct);
    }

    /// <summary>Peças que a loja mostra: ativas e, se a loja oculta as sem estoque, com saldo disponível.</summary>
    private async Task<IQueryable<Part>> VisibleAsync(CancellationToken ct)
    {
        var hideOutOfStock = await db.StoreSettings.Select(s => (bool?)s.HideOutOfStock).SingleOrDefaultAsync(ct) ?? false;
        var parts = db.Parts.AsNoTracking().Where(p => p.Status == PartStatus.Active);
        return hideOutOfStock ? parts.Where(p => db.Stocks.Any(s => s.PartId == p.Id && s.OnHand - s.Reserved > 0)) : parts;
    }
}
