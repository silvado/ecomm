using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Ecommerce.Domain.Catalog;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Channels.Meta;

/// <summary>
/// Item do catálogo da Meta montado da peça (campos de https://developers.facebook.com/docs/commerce-platform/catalog/fields).
/// <see cref="Id"/> é o id da peça (id do conteúdo, único no catálogo). Preço com ponto e a moeda ("350.00 BRL").
/// </summary>
public sealed record MetaItem(
    string Id, string Title, string Description, string Availability, string Condition, string Price, string Link, string? ImageLink,
    IReadOnlyList<string> AdditionalImageLinks, string Brand, int Quantity)
{
    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["id"] = Id, ["title"] = Title, ["description"] = Description, ["availability"] = Availability, ["condition"] = Condition,
            ["price"] = Price, ["link"] = Link, ["brand"] = Brand, ["quantity_to_sell_on_facebook"] = Quantity,
        };
        if (ImageLink is not null) json["image_link"] = ImageLink;
        if (AdditionalImageLinks.Count > 0) json["additional_image_link"] = string.Join(',', AdditionalImageLinks);
        return json;
    }
}

/// <summary>
/// Peças da loja do escopo como itens do catálogo da Meta. Entram as ativas com foto; peça inativa ou sem saldo vai como
/// "out of stock" (o item fica no catálogo, sem venda). Link e imagens apontam para a loja virtual (subdomínio da
/// plataforma, que sempre responde pela loja); a imagem sai em JPEG (a Meta não aceita WebP).
/// HIPÓTESE: marca = marca do primeiro veículo compatível; sem compatibilidade, o nome da loja.
/// </summary>
public static class MetaCatalogItems
{
    public const int TitleMaxLength = 200;
    public const int DescriptionMaxLength = 9_999;
    public const int MaxAdditionalImages = 9;

    private static readonly Dictionary<PartCondition, string> Conditions = new()
    {
        [PartCondition.New] = "new", [PartCondition.Used] = "used", [PartCondition.Refurbished] = "refurbished",
    };

    /// <param name="partIds">Nulo = todas as peças ativas com foto (feed); senão, estas (mesmo inativas, para tirar de venda).</param>
    public static async Task<List<MetaItem>> LoadAsync(TenantDbContext db, string origin, string storeName, IReadOnlyCollection<Guid>? partIds, CancellationToken ct)
    {
        var query = db.Parts.AsNoTracking().Where(p => p.Photos.Any());
        query = partIds is null ? query.Where(p => p.Status == PartStatus.Active) : query.Where(p => partIds.Contains(p.Id));
        var parts = await query
            .Select(p => new
            {
                p.Id, p.Title, p.Description, p.Condition, p.Price, p.Status,
                Photos = p.Photos.OrderBy(f => f.Position).Select(f => f.Id).ToList(),
                Available = db.Stocks.Where(s => s.PartId == p.Id).Select(s => s.OnHand - s.Reserved).FirstOrDefault(),
                Brand = (from c in db.PartCompatibilities
                         join v in db.VehicleVersions on c.VehicleVersionId equals v.Id
                         join m in db.VehicleModels on v.ModelId equals m.Id
                         join b in db.VehicleBrands on m.BrandId equals b.Id
                         where c.PartId == p.Id
                         orderby c.CreatedAt
                         select b.Name).FirstOrDefault(),
            })
            .OrderBy(p => p.Id).ToListAsync(ct);

        return parts.Select(p =>
        {
            var quantity = p.Status == PartStatus.Active ? Math.Max(0, p.Available) : 0;
            var images = p.Photos.Select(id => $"{origin}/api/loja/fotos/{id}/catalogo.jpg").ToList();
            var description = string.IsNullOrWhiteSpace(p.Description) ? p.Title : p.Description;
            return new MetaItem(p.Id.ToString(), Cut(p.Title, TitleMaxLength), Cut(description, DescriptionMaxLength),
                quantity > 0 ? "in stock" : "out of stock", Conditions[p.Condition],
                p.Price.ToString("0.00", CultureInfo.InvariantCulture) + " BRL",
                $"{origin}/peca/{p.Id}-{Slug(p.Title)}", images.FirstOrDefault(), images.Skip(1).Take(MaxAdditionalImages).ToList(),
                Cut(p.Brand ?? storeName, 100), quantity);
        }).ToList();
    }

    /// <summary>CSV do feed (cabeçalho com os nomes da Meta; campos entre aspas quando preciso).</summary>
    public static string Csv(IEnumerable<MetaItem> items)
    {
        var csv = new StringBuilder("id,title,description,availability,condition,price,link,image_link,additional_image_link,brand,quantity_to_sell_on_facebook\n");
        foreach (var i in items)
            csv.AppendJoin(',', new[] { i.Id, i.Title, i.Description, i.Availability, i.Condition, i.Price, i.Link, i.ImageLink ?? string.Empty,
                string.Join(',', i.AdditionalImageLinks), i.Brand, i.Quantity.ToString(CultureInfo.InvariantCulture) }.Select(Quote)).Append('\n');
        return csv.ToString();
    }

    /// <summary>Mesmo endereço da loja virtual ("/peca/{id}-{titulo}"; o id é o que vale).</summary>
    public static string Slug(string title)
    {
        var plain = new StringBuilder();
        foreach (var c in title.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            plain.Append(char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        }
        return string.Join('-', plain.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Quote(string value)
    {
        var text = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        return text.IndexOfAny([',', '"']) >= 0 || text.StartsWith(' ') ? $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : text;
    }

    private static string Cut(string text, int max) => text.Length > max ? text[..max] : text;
}
