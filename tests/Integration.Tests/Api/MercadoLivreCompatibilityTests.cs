using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Ecommerce.Domain.Channels;
using Ecommerce.Infrastructure.Channels.MercadoLivre;
using Ecommerce.Integration.Tests.Infrastructure;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF21 CA3 — compatibilidades da peça no anúncio do ML (ML simulado com catálogo de veículos de exemplo): família sem motor
/// quando a peça serve em todas as versões do ano, com motor quando não, veículo que não casa fica de fora e reenvio.
/// </summary>
public sealed class MercadoLivreCompatibilityTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    // Gol: as duas versões G5 cobrem 2009–2012; a G6 começa em 2013. Ford não existe no catálogo simulado do ML.
    private const string VehiclesCsv = """
        marca;modelo;motorizacao;ano_inicial;ano_final
        Volkswagen;Gol;1.0 8V Flex (G5);2009;2012
        Volkswagen;Gol;1.6 8V Flex (G5);2009;2012
        Volkswagen;Gol;1.0 8V Flex (G6);2013;2016
        Ford;Ka;1.0 Ti-VCT Flex;2015;2021
        """;

    private static readonly string[] Oem = ["5U0941015"];

    private sealed record StartDto(string AuthorizationUrl);
    private sealed record IdDto(Guid Id);
    private sealed record Option(Guid Id, string Name);
    private sealed record VersionOption(Guid Id, string Engine, int YearFrom, int? YearTo);
    private sealed record ListingDto(Guid Id, string Channel, string? ExternalId);
    private sealed record ReportDto(int Sent, List<string> Unmapped, string? Error);
    private sealed record PublishedDto(List<ListingDto> Listings, ReportDto? Compatibilities);

    private static string NewId() => Random.Shared.NextInt64(100_000_000, 999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task<string> ConnectedOwnerAsync()
    {
        Assert.True((await api.ImportVehiclesAsync(VehiclesCsv)).Succeeded);
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var start = (await (await SendAsync(api.PanelClient(), owner, HttpMethod.Post, "/api/painel/canais/mercado-livre/conectar")).Content.ReadFromJsonAsync<StartDto>())!;
        var state = Uri.UnescapeDataString(start.AuthorizationUrl.Split("state=")[1].Split('&')[0]);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(api.PanelClient(), owner, HttpMethod.Post, "/api/painel/canais/mercado-livre/retorno",
            new { code = FakeMercadoLivreAuth.CodePrefix + NewId(), state })).StatusCode);
        return owner;
    }

    private async Task<Dictionary<string, Guid>> VersionsAsync(string token)
    {
        var result = new Dictionary<string, Guid>();
        foreach (var brand in (await (await GetAsync(api.PanelClient(), token, "/api/painel/veiculos/marcas")).Content.ReadFromJsonAsync<List<Option>>())!)
            foreach (var model in (await (await GetAsync(api.PanelClient(), token, $"/api/painel/veiculos/marcas/{brand.Id}/modelos")).Content.ReadFromJsonAsync<List<Option>>())!)
                foreach (var v in (await (await GetAsync(api.PanelClient(), token, $"/api/painel/veiculos/modelos/{model.Id}/versoes")).Content.ReadFromJsonAsync<List<VersionOption>>())!)
                    result[$"{model.Name} {v.Engine}"] = v.Id;
        return result;
    }

    private async Task<Guid> PartAsync(string token, params (Guid Version, int? From, int? To)[] compatibilities)
    {
        var created = await SendAsync(api.PanelClient(), token, HttpMethod.Post, "/api/painel/pecas", new
        {
            internalCode = "C-" + NewId()[..6], title = "Farol dianteiro esquerdo Gol", condition = "used", price = 350m, quantity = 1, oemCodes = Oem,
        });
        var id = (await created.Content.ReadFromJsonAsync<IdDto>())!.Id;
        foreach (var (version, from, to) in compatibilities)
            Assert.Equal(HttpStatusCode.Created, (await SendAsync(api.PanelClient(), token, HttpMethod.Post, $"/api/painel/pecas/{id}/compatibilidades",
                new { vehicleVersionId = version, yearFrom = from, yearTo = to })).StatusCode);
        using var photo = new HttpRequestMessage(HttpMethod.Post, $"/api/painel/pecas/{id}/fotos") { Content = new ByteArrayContent(TestImages.Jpeg(800, 600)) };
        photo.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Created, (await api.PanelClient().SendAsync(photo)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(api.PanelClient(), token, HttpMethod.Put, $"/api/painel/pecas/{id}/situacao", new { status = "active" })).StatusCode);
        return id;
    }

    private async Task<PublishedDto> PublishAsync(string token, Guid partId)
    {
        var response = await SendAsync(api.PanelClient(), token, HttpMethod.Post, $"/api/painel/pecas/{partId}/mercado-livre/anunciar", new
        {
            categoryId = FakeMercadoLivreApi.HeadlightCategory, listingTypeId = "gold_special",
            attributes = new[]
            {
                new { id = "BRAND", valueId = (string?)null, valueName = (string?)"VW" },
                new { id = "ITEM_CONDITION", valueId = (string?)"2230581", valueName = (string?)null },
                new { id = "PART_NUMBER", valueId = (string?)null, valueName = (string?)"5U0941015" },
            },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PublishedDto>())!;
    }

    private static List<(string Brand, string Model, string Year, string? Engine)> Families(string itemId) =>
        FakeMercadoLivreApi.CompatibilitiesOf(itemId).Select(f =>
        {
            var a = ((JsonArray)f["attributes"]!).OfType<JsonObject>().ToDictionary(x => (string)x["id"]!, x => (string?)x["value_id"] ?? (string?)x["value_name"]);
            return (a["BRAND"]!, a["MODEL"]!, a["VEHICLE_YEAR"]!, a.GetValueOrDefault("ENGINE"));
        }).ToList();

    [Fact]
    public async Task Peca_que_serve_em_todas_as_versoes_do_ano_vai_sem_motor_logo_ao_publicar()
    {
        var owner = await ConnectedOwnerAsync();
        var v = await VersionsAsync(owner);
        var partId = await PartAsync(owner, (v["Gol 1.0 8V Flex (G5)"], null, null), (v["Gol 1.6 8V Flex (G5)"], null, null));

        var published = await PublishAsync(owner, partId);

        var itemId = published.Listings.Single(l => l.Channel == SalesChannel.MercadoLivre).ExternalId!;
        Assert.Equal((4, 0, (string?)null), (published.Compatibilities!.Sent / 3, published.Compatibilities.Unmapped.Count, published.Compatibilities.Error));
        Assert.Equal([("60249", "62109", "2009", (string?)null), ("60249", "62109", "2010", null), ("60249", "62109", "2011", null), ("60249", "62109", "2012", null)],
            Families(itemId));
        Assert.All(FakeMercadoLivreApi.CompatibilitiesOf(itemId), f => Assert.Equal("DEFAULT", (string?)f["creation_source"]));
    }

    [Fact]
    public async Task So_uma_versao_vai_com_o_motor_e_veiculo_fora_do_catalogo_do_ML_fica_de_fora()
    {
        var owner = await ConnectedOwnerAsync();
        var v = await VersionsAsync(owner);
        var partId = await PartAsync(owner, (v["Gol 1.6 8V Flex (G5)"], 2011, 2012), (v["Ka 1.0 Ti-VCT Flex"], 2015, 2016));

        var published = await PublishAsync(owner, partId);

        var itemId = published.Listings.Single(l => l.Channel == SalesChannel.MercadoLivre).ExternalId!;
        // "1.6 8V Flex (G5)" casa com "1.6 8V" (mesma cilindrada, mais palavras em comum que as outras opções).
        Assert.Equal([("60249", "62109", "2011", (string?)"SIM-ENG-62109-1"), ("60249", "62109", "2012", "SIM-ENG-62109-1")], Families(itemId));
        // Ka tem uma versão só na tabela: a família iria sem motor, mas a marca não existe no catálogo (simulado) do ML.
        Assert.Equal(["Ford Ka 2015", "Ford Ka 2016"], published.Compatibilities!.Unmapped);
    }

    [Fact]
    public async Task Reenviar_pelo_anuncio_depois_de_incluir_veiculos_na_peca()
    {
        var owner = await ConnectedOwnerAsync();
        var v = await VersionsAsync(owner);
        var partId = await PartAsync(owner);
        var published = await PublishAsync(owner, partId);
        Assert.Equal((0, 0), (published.Compatibilities!.Sent, published.Compatibilities.Unmapped.Count));
        var listing = published.Listings.Single(l => l.Channel == SalesChannel.MercadoLivre);

        Assert.Equal(HttpStatusCode.Created, (await SendAsync(api.PanelClient(), owner, HttpMethod.Post, $"/api/painel/pecas/{partId}/compatibilidades",
            new { vehicleVersionId = v["Gol 1.0 8V Flex (G6)"], yearFrom = 2013, yearTo = 2014 })).StatusCode);
        var report = (await (await SendAsync(api.PanelClient(), owner, HttpMethod.Post, $"/api/painel/anuncios/{listing.Id}/compatibilidades"))
            .Content.ReadFromJsonAsync<ReportDto>())!;

        // G6 é a única versão do Gol nesses anos: vai sem motor.
        Assert.Equal((6, (string?)null), (report.Sent, report.Error));
        Assert.Equal([("60249", "62109", "2013", (string?)null), ("60249", "62109", "2014", null)], Families(listing.ExternalId!));
    }
}
