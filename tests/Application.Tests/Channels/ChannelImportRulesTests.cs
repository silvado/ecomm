using Ecommerce.Application.Channels;
using Ecommerce.Domain.Catalog;
using Ecommerce.Domain.Channels;

namespace Ecommerce.Application.Tests.Channels;

/// <summary>RF11 CA2 — como um anúncio do Mercado Livre vira peça.</summary>
public sealed class ChannelImportRulesTests
{
    private static ChannelItemSnapshot Item(string status = "active", string? condition = "used", string? sku = null, string? customField = null,
        bool variations = false, decimal price = 350m, string title = "Farol dianteiro Gol", IReadOnlyList<AttributeValue>? attributes = null) =>
        new("MLB123456", title, price, 2, status, condition, customField, "Sem trincas.", ["https://http2.mlstatic.com/a.jpg"], "MLB1747", "gold_pro",
            [.. attributes ?? [], .. sku is null ? Array.Empty<AttributeValue>() : [new AttributeValue("SELLER_SKU", null, sku)]],
            [new AttributeValue("WARRANTY_TYPE", "2230280", "Garantia do vendedor"), new AttributeValue("WARRANTY_TIME", null, "90 dias")], variations);

    [Fact]
    public void Anuncio_ativo_vira_peca_com_o_SKU_como_codigo()
    {
        var (item, reason) = ChannelImportRules.Map(Item(sku: "far-001", attributes: [new AttributeValue("PART_NUMBER", null, "5U0941015"), new AttributeValue("BRAND", "1", "VW")]));
        Assert.Null(reason);
        Assert.Equal(("FAR-001", ListingStatus.Active, 2), (item!.InternalCode, item.ListingStatus, item.Quantity));
        Assert.Equal(("Farol dianteiro Gol", "Sem trincas.", PartCondition.Used, 350m), (item.Details.Title, item.Details.Description, item.Details.Condition, item.Details.Price));
        Assert.Equal(["5U0941015"], item.Details.OemCodes!);
        Assert.Equal(("MLB1747", "gold_pro", "Garantia do vendedor", "90 dias"), (item.Options.CategoryId, item.Options.ListingTypeId, item.Options.WarrantyType, item.Options.WarrantyTime));
        Assert.DoesNotContain(item.Options.Attributes, a => a.Id == "SELLER_SKU");
        Assert.Contains(item.Options.Attributes, a => a.Id == "BRAND");
    }

    [Fact]
    public void Sem_SKU_usa_o_campo_do_vendedor_ou_o_id_do_anuncio()
    {
        Assert.Equal("ABC-9", ChannelImportRules.Map(Item(customField: "abc-9")).Item!.InternalCode);
        Assert.Equal("MLB123456", ChannelImportRules.Map(Item()).Item!.InternalCode);
        Assert.Equal("MLB123456", ChannelImportRules.Map(Item(sku: new string('x', Part.InternalCodeMaxLength + 1))).Item!.InternalCode);
    }

    [Fact]
    public void Pausado_la_entra_pausado_pelo_lojista()
    {
        Assert.Equal(ListingStatus.PausedByUser, ChannelImportRules.Map(Item(status: "paused")).Item!.ListingStatus);
    }

    [Theory]
    [InlineData("new", null, PartCondition.New)]
    [InlineData("used", null, PartCondition.Used)]
    [InlineData("not_specified", null, PartCondition.Used)]
    [InlineData("used", "Recondicionado", PartCondition.Refurbished)]
    public void Condicao_vem_do_anuncio(string condition, string? itemCondition, PartCondition expected)
    {
        var attributes = itemCondition is null ? null : new[] { new AttributeValue("ITEM_CONDITION", "2230582", itemCondition) };
        Assert.Equal(expected, ChannelImportRules.Map(Item(condition: condition, attributes: attributes)).Item!.Details.Condition);
    }

    [Theory]
    [InlineData("closed", false, 350, "encerrado")]
    [InlineData("under_review", false, 350, "encerrado")]
    [InlineData("active", true, 350, "variações")]
    [InlineData("active", false, 0, "sem preço")]
    public void Anuncio_que_nao_entra_tem_motivo(string status, bool variations, int price, string reason)
    {
        var (item, why) = ChannelImportRules.Map(Item(status: status, variations: variations, price: price));
        Assert.Null(item);
        Assert.Contains(reason, why!, StringComparison.Ordinal);
    }

    [Fact]
    public void Titulo_longo_e_cortado_no_limite_da_peca()
    {
        Assert.Equal(Part.TitleMaxLength, ChannelImportRules.Map(Item(title: new string('a', 200))).Item!.Details.Title.Length);
    }
}
