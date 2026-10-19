using System.Net;
using System.Text;
using Ecommerce.Infrastructure.Channels.MercadoLivre;

namespace Ecommerce.Integration.Tests.Channels;

/// <summary>
/// Contrato da importação de anúncios (RF11 CA2, sem banco): página da busca do vendedor no modo scan, leitura do anúncio
/// (título ou family_name, fotos, atributos, variações) e a trava das fotos — só o servidor de imagens do ML, por https.
/// Fonte: https://developers.mercadolivre.com.br/itens-e-buscas.
/// </summary>
public sealed class MercadoLivreImportContractTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static MercadoLivreApiClient Api(StubHandler handler) =>
        new(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(new MercadoLivreOptions { ApiUrl = "https://api.ml.test" }));

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Busca_do_vendedor_le_os_ids_e_segue_o_scroll_id()
    {
        var handler = new StubHandler(_ => Json("""{"seller_id":"123","results":["MLB1","MLB2"],"scroll_id":"abc=="}"""));
        var (ids, next) = await Api(handler).SearchSellerItemsAsync("token", "123", "prev cursor", CancellationToken.None);
        Assert.Equal(["MLB1", "MLB2"], ids);
        Assert.Equal("abc==", next);
        var uri = Assert.Single(handler.Requests);
        Assert.Equal("/users/123/items/search", uri.AbsolutePath);
        Assert.Contains("search_type=scan", uri.Query, StringComparison.Ordinal);
        Assert.Contains("scroll_id=prev%20cursor", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anuncio_le_family_name_fotos_atributos_variacoes_e_descricao()
    {
        var handler = new StubHandler(r => r.RequestUri!.AbsolutePath.EndsWith("/description", StringComparison.Ordinal)
            ? Json("""{"text":"","plain_text":"Sem trincas."}""")
            : Json("""
                {"id":"MLB1","family_name":"Farol Gol","price":350.5,"available_quantity":2,"status":"paused","condition":"used",
                 "category_id":"MLB1747","listing_type_id":"gold_special","seller_custom_field":"FAR-1",
                 "pictures":[{"id":"p1","secure_url":"https://http2.mlstatic.com/a.jpg"},{"id":"p2","url":"https://http2.mlstatic.com/b.jpg"}],
                 "attributes":[{"id":"BRAND","value_id":null,"value_name":"VW"},{"id":"SELLER_SKU","value_name":"FAR-001"}],
                 "sale_terms":[{"id":"WARRANTY_TYPE","value_name":"Sem garantia"}],"shipping":{"mode":"me2","dimensions":"10x20x30,500"},
                 "variations":[{"id":14979332589,"attribute_combinations":[{"id":"SIDE","name":"Lado","value_id":"1","value_name":"Esquerdo"}],
                   "price":360,"available_quantity":1,"sold_quantity":0,"picture_ids":["p2"],"seller_custom_field":null,"catalog_product_id":null,
                   "attributes":[{"id":"SELLER_SKU","name":"SKU","value_id":null,"value_name":"FAR-1E"}]},
                  {"id":14979332592,"attribute_combinations":[{"id":"SIDE","name":"Lado","value_id":"2","value_name":"Direito"}],
                   "price":360,"available_quantity":1,"sold_quantity":0,"picture_ids":["p9"],"seller_custom_field":"DIR"}]}
                """));
        var item = (await Api(handler).GetItemSnapshotAsync("token", "MLB1", CancellationToken.None))!;
        Assert.Equal(("MLB1", "Farol Gol", 350.5m, 2, "paused", "used", "FAR-1"), (item.Id, item.Title, item.Price, item.Quantity, item.Status, item.Condition, item.SellerCustomField));
        Assert.Equal(["https://http2.mlstatic.com/a.jpg", "https://http2.mlstatic.com/b.jpg"], item.PictureUrls);
        Assert.Equal(("Sem trincas.", true, 2), (item.Description, item.HasVariations, item.Attributes.Count));
        Assert.Equal("FAR-001", item.Attributes.Single(a => a.Id == "SELLER_SKU").ValueName);
        Assert.Equal("10x20x30,500", item.ShippingDimensions);
        // Variações (https://developers.mercadolivre.com.br/pt_br/variacoes): foto pelo id; os atributos pedem include_attributes=all.
        Assert.Equal(2, item.Variations.Count);
        var (left, right) = (item.Variations[0], item.Variations[1]);
        Assert.Equal(("14979332589", "Esquerdo", 360m, 1), (left.Id, left.Combinations.Single().ValueName, left.Price, left.Quantity));
        Assert.Equal(["https://http2.mlstatic.com/b.jpg"], left.PictureUrls);
        Assert.Equal("FAR-1E", left.Attributes.Single(a => a.Id == "SELLER_SKU").ValueName);
        Assert.Equal(("DIR", 0), (right.SellerCustomField, right.PictureUrls.Count)); // foto que o anúncio não tem fica de fora
        Assert.Contains("include_attributes=all", handler.Requests.First(r => r.AbsolutePath == "/items/MLB1").Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://http2.mlstatic.com/D_NQ_NP_123.jpg", true)]
    [InlineData("https://mlstatic.com/a.jpg", true)]
    [InlineData("http://http2.mlstatic.com/a.jpg", false)] // sem https
    [InlineData("https://mlstatic.com.evil.test/a.jpg", false)]
    [InlineData("https://evilmlstatic.com/a.jpg", false)]
    [InlineData("https://http2.mlstatic.com:8443/a.jpg", false)]
    [InlineData("https://169.254.169.254/latest/meta-data", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("", false)]
    public void Foto_so_do_servidor_de_imagens_do_ML(string url, bool allowed)
    {
        Assert.Equal(allowed, MercadoLivreApiClient.IsPictureUrl(url));
    }

    [Fact]
    public async Task Foto_de_outro_endereco_nem_e_pedida()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        Assert.Null(await Api(handler).DownloadPictureAsync("https://intranet.local/x.jpg", CancellationToken.None));
        Assert.Empty(handler.Requests);
        Assert.Equal(new byte[] { 1, 2, 3 }, await Api(handler).DownloadPictureAsync("https://http2.mlstatic.com/x.jpg", CancellationToken.None));
    }

    [Fact]
    public async Task Compatibilidades_do_anuncio_separam_as_do_vendedor_das_do_catalogo()
    {
        // Resposta de exemplo da página de compatibilidades (source SELLER com produto; CATALOGO só com marca e total).
        var handler = new StubHandler(_ => Json("""
            {"products":[
              {"id":"bcbd413f","domain_id":"MLM-CARS_AND_VANS_FOR_COMPATIBILITIES","catalog_product_id":"MLM15847548",
               "catalog_product_name":"Volkswagen Jetta 2010 GLI Manual 5","source":"SELLER","note":"Solo modelos de caja automática"},
              {"id":"58bd413f","domain_id":"MLM-CARS_AND_VANS_FOR_COMPATIBILITIES","catalog_product_id":"MLM15847546",
               "catalog_product_name":"Volkswagen Jetta 2010 GLI Automática 6","source":"SELLER"},
              {"id":null,"domain_id":null,"catalog_product_id":null,"catalog_product_name":"Volkswagen","source":"CATALOGO","reputation":null,"total":8},
              {"id":null,"domain_id":null,"catalog_product_id":null,"catalog_product_name":"Audi","source":"CATALOGO","reputation":null,"total":7}
            ]}
            """));
        var (ids, catalogOnly) = await Api(handler).ItemCompatibilitiesAsync("token", "MLB1", CancellationToken.None);
        Assert.Equal(["MLM15847548", "MLM15847546"], ids);
        Assert.Equal(15, catalogOnly);
        Assert.Equal("/items/MLB1/compatibilities", Assert.Single(handler.Requests).AbsolutePath);

        var none = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Empty((await Api(none).ItemCompatibilitiesAsync("token", "MLB2", CancellationToken.None)).ProductIds);
    }

    [Fact]
    public async Task Veiculo_do_catalogo_le_marca_modelo_ano_e_motor()
    {
        var handler = new StubHandler(r => r.RequestUri!.AbsolutePath.EndsWith("/MLB1", StringComparison.Ordinal)
            ? Json("""
                {"id":"MLB1","status":"active","domain_id":"MLB-CARS_AND_VANS","name":"Volkswagen Gol 2010 1.6 8V",
                 "attributes":[{"id":"BRAND","name":"Marca","value_id":"60249","value_name":"Volkswagen","values":[{"id":"60249","name":"Volkswagen"}]},
                               {"id":"MODEL","name":"Modelo","value_id":"62109","value_name":"Gol"},
                               {"id":"VEHICLE_YEAR","name":"Ano","value_id":"1","value_name":"2010"},
                               {"id":"ENGINE","name":"Motor","value_id":"9","value_name":"1.6 8V"}]}
                """)
            : Json("""{"id":"MLB2","attributes":[{"id":"BRAND","value_name":"Volkswagen"}]}"""));
        Assert.Equal(new Ecommerce.Application.Channels.ChannelVehicle("Volkswagen", "Gol", 2010, "1.6 8V"),
            await Api(handler).VehicleProductAsync("token", "MLB1", CancellationToken.None));
        Assert.Null(await Api(handler).VehicleProductAsync("token", "MLB2", CancellationToken.None)); // sem modelo e ano
        Assert.Equal("/products/MLB1", handler.Requests[0].AbsolutePath);
    }
}
