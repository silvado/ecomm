using Ecommerce.Application.Channels;
using Ecommerce.Domain.Catalog;
using Ecommerce.Domain.Channels;

namespace Ecommerce.Application.Tests.Channels;

/// <summary>RF11 CA2 — como um anúncio do Mercado Livre vira peça.</summary>
public sealed class ChannelImportRulesTests
{
    private static ChannelItemSnapshot Item(string status = "active", string? condition = "used", string? sku = null, string? customField = null,
        IReadOnlyList<ChannelItemVariation>? variations = null, decimal price = 350m, string title = "Farol dianteiro Gol",
        IReadOnlyList<AttributeValue>? attributes = null) =>
        new("MLB123456", title, price, 2, status, condition, customField, "Sem trincas.", ["https://http2.mlstatic.com/a.jpg"], "MLB1747", "gold_pro",
            [.. attributes ?? [], .. sku is null ? Array.Empty<AttributeValue>() : [new AttributeValue("SELLER_SKU", null, sku)]],
            [new AttributeValue("WARRANTY_TYPE", "2230280", "Garantia do vendedor"), new AttributeValue("WARRANTY_TIME", null, "90 dias")], variations ?? []);

    private static ChannelItemVariation Variation(string id, string side, int quantity = 1, decimal price = 350m, string? sku = null,
        IReadOnlyList<string>? pictures = null) =>
        new(id, [new AttributeValue("SIDE", null, side)], price, quantity, pictures ?? [], null,
            sku is null ? [] : [new AttributeValue("SELLER_SKU", null, sku)]);

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
    [InlineData("closed", 350, "encerrado")]
    [InlineData("under_review", 350, "encerrado")]
    [InlineData("active", 0, "sem preço")]
    public void Anuncio_que_nao_entra_tem_motivo(string status, int price, string reason)
    {
        var (item, why) = ChannelImportRules.Map(Item(status: status, price: price));
        Assert.Null(item);
        Assert.Contains(reason, why!, StringComparison.Ordinal);
    }

    [Fact]
    public void Cada_variacao_vira_uma_peca_com_a_combinacao_no_titulo()
    {
        var item = Item(sku: "far-1", status: "paused", variations:
        [
            Variation("101", "Esquerdo", quantity: 1, sku: "far-1e", pictures: ["https://http2.mlstatic.com/e.jpg"]),
            Variation("102", "Direito", quantity: 3, price: 0),
        ]);
        Assert.Contains("variações", ChannelImportRules.Map(item).Reason!, StringComparison.Ordinal);

        var (variations, reason) = ChannelImportRules.MapVariations(item);
        Assert.Null(reason);
        Assert.Equal(2, variations.Count);
        var (left, right) = (variations[0].Item!, variations[1].Item!);
        Assert.Equal(("FAR-1E", "Farol dianteiro Gol - Esquerdo", 1, 350m), (left.InternalCode, left.Details.Title, left.Quantity, left.Details.Price));
        Assert.Equal(("FAR-1-102", "Farol dianteiro Gol - Direito", 3, 350m), (right.InternalCode, right.Details.Title, right.Quantity, right.Details.Price));
        Assert.Equal(["https://http2.mlstatic.com/e.jpg"], variations[0].PictureUrls);
        Assert.Equal(["https://http2.mlstatic.com/a.jpg"], variations[1].PictureUrls); // sem foto própria: as do anúncio
        Assert.Equal(("Sem trincas.", PartCondition.Used), (right.Details.Description, right.Details.Condition));
        Assert.Equal("MLB123456-102", ChannelImportRules.MapVariations(Item(variations: [Variation("102", "Direito")])).Variations[0].Item!.InternalCode);
    }

    [Fact]
    public void Variacao_sem_preco_ou_anuncio_encerrado_fica_de_fora()
    {
        var (variations, _) = ChannelImportRules.MapVariations(Item(price: 0, variations: [Variation("1", "Esquerdo", price: 0), Variation("2", "Direito", price: 90)]));
        Assert.Null(variations[0].Item);
        Assert.Contains("sem preço", variations[0].Reason!, StringComparison.Ordinal);
        Assert.Equal(90m, variations[1].Item!.Details.Price);

        var (none, reason) = ChannelImportRules.MapVariations(Item(status: "closed", variations: [Variation("1", "Esquerdo")]));
        Assert.Empty(none);
        Assert.Contains("encerrado", reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Titulo_longo_da_variacao_guarda_a_combinacao()
    {
        var title = ChannelImportRules.MapVariations(Item(title: new string('a', 200), variations: [Variation("1", "Esquerdo")])).Variations[0].Item!.Details.Title;
        Assert.Equal(Part.TitleMaxLength, title.Length);
        Assert.EndsWith(" - Esquerdo", title, StringComparison.Ordinal);
    }

    [Fact]
    public void Titulo_longo_e_cortado_no_limite_da_peca()
    {
        Assert.Equal(Part.TitleMaxLength, ChannelImportRules.Map(Item(title: new string('a', 200))).Item!.Details.Title.Length);
    }

    private static ChannelImportRules.Package? Package(string? shipping, params (string Id, string Value)[] attributes) =>
        ChannelImportRules.PackageDimensions(Item(attributes: attributes.Select(a => new AttributeValue(a.Id, null, a.Value)).ToList()) with { ShippingDimensions = shipping });

    [Fact]
    public void Medidas_da_embalagem_vem_dos_atributos_com_unidade()
    {
        Assert.Equal(new ChannelImportRules.Package(40, 30, 21, 1500),
            Package("1x1x1,1", ("SELLER_PACKAGE_LENGTH", "40 cm"), ("SELLER_PACKAGE_WIDTH", "300 mm"), ("SELLER_PACKAGE_HEIGHT", "20,5"), ("SELLER_PACKAGE_WEIGHT", "1.5 kg")));
        var (item, _) = ChannelImportRules.Map(Item(attributes: [new("SELLER_PACKAGE_LENGTH", null, "40"), new("SELLER_PACKAGE_WIDTH", null, "30"),
            new("SELLER_PACKAGE_HEIGHT", null, "20"), new("SELLER_PACKAGE_WEIGHT", null, "800")]));
        Assert.Equal((40, 30, 20, 800), (item!.Details.LengthCm, item.Details.WidthCm, item.Details.HeightCm, item.Details.WeightG));
    }

    [Fact]
    public void Sem_atributos_usa_o_shipping_dimensions()
    {
        // HIPÓTESE: altura x largura x comprimento, peso em gramas.
        Assert.Equal(new ChannelImportRules.Package(30, 20, 10, 500), Package("10x20x30,500"));
        Assert.Equal(new ChannelImportRules.Package(30, 20, 10, 500), Package("10x20x30,500", ("SELLER_PACKAGE_LENGTH", "40"))); // atributo pela metade
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("10x20,500")]
    [InlineData("10x20x30")]
    [InlineData("10x20x0,500")]
    [InlineData("10x20x2000,500")] // acima de 1.000 cm
    [InlineData("10x20x30,meio quilo")]
    [InlineData("10x20x30,99999999999999999999")]
    public void Medida_incompleta_ou_fora_do_limite_fica_em_branco(string? shipping)
    {
        Assert.Null(Package(shipping));
    }
}