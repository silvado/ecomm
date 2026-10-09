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
                               p.UpdatedAt))
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PartPage(items, total, page, size);
    }

    public async Task<PartView?> GetAsync(Guid partId, CancellationToken ct = default)
    {
        var part = await db.Parts.AsNoTracking().Include(p => p.OemCodes).SingleOrDefaultAsync(p => p.Id == partId, ct);
        if (part is null) return null;
        var stock = await db.Stocks.AsNoTracking().Where(s => s.PartId == partId)
            .Select(s => new StockView(s.OnHand, s.Reserved, s.OnHand - s.Reserved)).SingleAsync(ct);
        return new PartView(part.Id, part.InternalCode, part.Title, part.Description, part.Condition, part.Price,
            part.LengthCm, part.WidthCm, part.HeightCm, part.WeightG, part.OemCodes.Select(c => c.Code).Order().ToList(),
            part.Status, stock, part.HasShippingDimensions, part.CreatedAt, part.UpdatedAt);
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static string OemOrEmpty(string text)
    {
        try { return PartOemCode.Normalize(text); }
        catch (ArgumentException) { return string.Empty; }
    }
}
