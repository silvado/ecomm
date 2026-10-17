using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Ecommerce.Application.Channels;
using Ecommerce.Domain.Catalog;
using Ecommerce.Infrastructure.Channels.MercadoLivre;
using Microsoft.Extensions.Options;

namespace Ecommerce.Integration.Tests.Channels;

/// <summary>
/// Contrato do anúncio no ML (sem banco): corpo do POST /items nos dois modelos (User Products e anterior) e leitura das
/// respostas — atributos com tags em lista ou objeto, motivos da recusa (cause[].message) e avisos do PUT.
/// Fontes: publicacao-de-produtos, preco-variacao, atributos e produto-sincronizacao-de-publicacoes em developers.mercadolivre.com.br.
/// </summary>
public sealed class MercadoLivreItemContractTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static MercadoLivreApiClient Api(HttpStatusCode status, string body, out StubHandler handler)
    {
        handler = new StubHandler(status, body);
        return new MercadoLivreApiClient(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(new MercadoLivreOptions { ApiUrl = "https://api.ml.test" }));
    }

    private static readonly ListingDraft Draft = new(Guid.NewGuid(), "FAR-001", "Farol dianteiro esquerdo Gol G5 com máscara negra e regulagem elétrica original",
        "desc", 350.5m, 2, PartCondition.Used, [Guid.NewGuid()]);

    private static MercadoLivreListingOptions Options(string? warranty = null) => new("MLB1747", "gold_pro",
        [new AttributeValue("BRAND", null, " VW "), new AttributeValue("ITEM_CONDITION", "2230581", null), new AttributeValue("GTIN", null, "")], warranty, warranty is null ? null : "3 meses");

    [Fact]
    public void Vendedor_User_Products_manda_family_name_sem_title_e_titulo_cortado_no_limite()
    {
        var item = MercadoLivreConnector.BuildItem(Draft, Options(), maxTitleLength: 60, userProductSeller: true, ["PIC-1"]);

        Assert.False(item.ContainsKey("title"));
        Assert.Equal(60, ((string)item["family_name"]!).Length);
        Assert.Equal(("MLB1747", "gold_pro", "BRL", "buy_it_now", "used", 350.5m, 2),
            ((string?)item["category_id"], (string?)item["listing_type_id"], (string?)item["currency_id"], (string?)item["buying_mode"], (string?)item["condition"],
                (decimal)item["price"]!, (int)item["available_quantity"]!));
        Assert.Equal("PIC-1", (string?)item["pictures"]![0]!["id"]);
        var attributes = ((JsonArray)item["attributes"]!).OfType<JsonObject>().ToList();
        Assert.Equal(["BRAND", "ITEM_CONDITION", "SELLER_SKU"], attributes.Select(a => (string)a["id"]!)); // vazio (GTIN) não vai
        Assert.Equal(("VW", "2230581", "FAR-001"), ((string?)attributes[0]["value_name"], (string?)attributes[1]["value_id"], (string?)attributes[2]["value_name"]));
        Assert.Equal("Sem garantia", (string?)item["sale_terms"]![0]!["value_name"]);
    }

    [Fact]
    public void Modelo_anterior_manda_title_e_garantia_com_tempo()
    {
        var item = MercadoLivreConnector.BuildItem(Draft with { Condition = PartCondition.Refurbished }, Options("Garantia do vendedor"), 200, userProductSeller: false, ["PIC-1"]);

        Assert.True(item.ContainsKey("title"));
        Assert.False(item.ContainsKey("family_name"));
        Assert.False(item.ContainsKey("condition")); // recondicionado vai só no atributo ITEM_CONDITION
        Assert.Equal(("WARRANTY_TYPE", "Garantia do vendedor", "WARRANTY_TIME", "3 meses"),
            ((string?)item["sale_terms"]![0]!["id"], (string?)item["sale_terms"]![0]!["value_name"], (string?)item["sale_terms"]![1]!["id"], (string?)item["sale_terms"]![1]!["value_name"]));
    }

    [Fact]
    public async Task Atributos_da_categoria_sem_read_only_fixed_e_com_tags_em_lista_ou_objeto()
    {
        var api = Api(HttpStatusCode.OK, """
            [{"id":"BRAND","name":"Marca","value_type":"string","value_max_length":255,"tags":["catalog_required","required"]},
             {"id":"ITEM_CONDITION","name":"Condição do item","value_type":"list","tags":{"hidden":true,"required":true},
              "values":[{"id":"2230284","name":"Novo"},{"id":"2230581","name":"Usado"}]},
             {"id":"GTIN","name":"Código universal","value_type":"string","tags":{"new_required":true}},
             {"id":"VOLUME","name":"Volume","value_type":"number_unit","tags":{},"allowed_units":[{"id":"L","name":"L"}],"default_unit":"L"},
             {"id":"PRODUCT_DATA_SOURCE","name":"Fonte","value_type":"string","tags":{"read_only":true}},
             {"id":"LINE","name":"Linha","value_type":"string","tags":{"fixed":true}}]
            """, out _);

        var used = await api.AttributesAsync("token", "MLB1747", isNew: false, CancellationToken.None);
        var fresh = await api.AttributesAsync("token", "MLB1747", isNew: true, CancellationToken.None);

        Assert.Equal(["BRAND", "ITEM_CONDITION", "GTIN", "VOLUME"], used.Select(a => a.Id));
        Assert.Equal([true, true, false, false], used.Select(a => a.Required));
        Assert.True(fresh.Single(a => a.Id == "GTIN").Required); // new_required só vale para peça nova
        Assert.Equal((2, "L"), (used[1].Values.Count, used[3].DefaultUnit));
    }

    [Fact]
    public async Task Recusa_traz_os_motivos_do_ML()
    {
        var api = Api(HttpStatusCode.BadRequest, """
            {"message":"Validation error","error":"validation_error","status":400,
             "cause":[{"code":"item.attribute.missing_required","message":"The attributes [BRAND] are required for category MLB1747."},
                      {"code":"item.price.invalid","message":"Price is lower than minimum."}]}
            """, out _);

        var e = await Assert.ThrowsAsync<ChannelValidationException>(() => api.CreateItemAsync("token", [], CancellationToken.None));

        Assert.Equal("The attributes [BRAND] are required for category MLB1747. Price is lower than minimum.", e.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Limite_fora_do_ar_ou_acesso_recusado_vao_para_a_fila_tentar_de_novo(HttpStatusCode status)
    {
        var api = Api(status, "{}", out _);

        await Assert.ThrowsAsync<ChannelConnectorException>(() => api.UpdateItemAsync("token", "MLB1", new JsonObject { ["status"] = "paused" }, CancellationToken.None));
    }

    [Fact]
    public async Task Aviso_do_PUT_e_anuncio_inexistente()
    {
        var withWarning = Api(HttpStatusCode.OK, """{"id":"MLB1","warnings":[{"code":"price.automation","message":"O preço não foi atualizado: automação ativa."}]}""", out var handler);
        Assert.Equal(["O preço não foi atualizado: automação ativa."], await withWarning.UpdateItemAsync("token", "MLB1", new JsonObject { ["price"] = 10m }, CancellationToken.None));
        Assert.Contains("\"price\":10", handler.LastBody, StringComparison.Ordinal);

        Assert.Null(await Api(HttpStatusCode.NotFound, """{"message":"not_found"}""", out _).GetItemAsync("token", "MLB404", CancellationToken.None));
    }
}
