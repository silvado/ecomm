using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ecommerce.Integration.Tests.Infrastructure;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>RF09 — compatibilidades da peça e busca por veículo (painel e loja pública).</summary>
public sealed class CompatibilityTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private const string Vehicles = """
        marca;modelo;motorizacao;ano_inicial;ano_final
        Volkswagen;Gol;1.0 8V Flex (G5);2009;2012
        Volkswagen;Gol;1.6 8V Flex (G5);2009;2012
        Volkswagen;Fox;1.0 8V Flex;2004;2009
        Fiat;Uno;1.0 Fire Flex;2004;2013
        """;

    private sealed record Option(Guid Id, string Name);
    private sealed record VersionOption(Guid Id, string Engine, int YearFrom, int? YearTo, bool Discontinued);
    private sealed record CompatibilityDto(Guid Id, Guid VehicleVersionId, string Brand, string Model, string Engine, int YearFrom, int? YearTo, bool Narrowed, bool Discontinued);
    private sealed record PartDto(Guid Id, List<CompatibilityDto> Compatibilities);
    private sealed record SummaryDto(Guid Id);
    private sealed record PageDto(List<SummaryDto> Items, int Total);

    private sealed record Catalog(Guid Gol, Guid Fox, Guid Gol10, Guid Gol16, Guid Fox10);

    private async Task<Catalog> VehiclesAsync(string token)
    {
        Assert.True((await api.ImportVehiclesAsync(Vehicles)).Succeeded);
        var client = api.PanelClient();
        var vw = (await (await GetAsync(client, token, "/api/painel/veiculos/marcas")).Content.ReadFromJsonAsync<List<Option>>())!.Single(b => b.Name == "Volkswagen");
        var models = (await (await GetAsync(client, token, $"/api/painel/veiculos/marcas/{vw.Id}/modelos")).Content.ReadFromJsonAsync<List<Option>>())!;
        var (gol, fox) = (models.Single(m => m.Name == "Gol").Id, models.Single(m => m.Name == "Fox").Id);
        var golVersions = (await (await GetAsync(client, token, $"/api/painel/veiculos/modelos/{gol}/versoes")).Content.ReadFromJsonAsync<List<VersionOption>>())!;
        var foxVersions = (await (await GetAsync(client, token, $"/api/painel/veiculos/modelos/{fox}/versoes")).Content.ReadFromJsonAsync<List<VersionOption>>())!;
        return new Catalog(gol, fox, golVersions.Single(v => v.Engine.StartsWith("1.0", StringComparison.Ordinal)).Id,
            golVersions.Single(v => v.Engine.StartsWith("1.6", StringComparison.Ordinal)).Id, foxVersions.Single().Id);
    }

    private async Task<Guid> PartAsync(string token, string title, bool active = true)
    {
        var created = await SendAsync(api.PanelClient(), token, HttpMethod.Post, "/api/painel/pecas", new
        {
            internalCode = "C-" + Guid.NewGuid().ToString("N")[..8], title, condition = "used", price = 99m, quantity = 1, oemCodes = Array.Empty<string>(),
        });
        var id = (await created.Content.ReadFromJsonAsync<SummaryDto>())!.Id;
        if (active)
        {
            using var photo = new HttpRequestMessage(HttpMethod.Post, $"/api/painel/pecas/{id}/fotos") { Content = new ByteArrayContent(TestImages.Jpeg(120, 90)) };
            photo.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Assert.Equal(HttpStatusCode.Created, (await api.PanelClient().SendAsync(photo)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(api.PanelClient(), token, HttpMethod.Put, $"/api/painel/pecas/{id}/situacao", new { status = "active" })).StatusCode);
        }
        return id;
    }

    private Task<HttpResponseMessage> AddAsync(string token, Guid partId, Guid versionId, int? from = null, int? to = null) =>
        SendAsync(api.PanelClient(), token, HttpMethod.Post, $"/api/painel/pecas/{partId}/compatibilidades",
            new { vehicleVersionId = versionId, yearFrom = from, yearTo = to });

    private async Task<List<Guid>> StoreSearchAsync(Ecommerce.Domain.Platform.Tenant tenant, string query) =>
        (await api.ClientFor($"{tenant.Slug}.plataforma.test").GetFromJsonAsync<PageDto>("/api/loja/pecas?" + query))!.Items.Select(p => p.Id).ToList();

    [Fact]
    public async Task Compatibilidade_aparece_na_peca_com_marca_modelo_motorizacao_e_anos()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var v = await VehiclesAsync(owner);
        var part = await PartAsync(owner, "Farol Gol G5", active: false);

        Assert.Equal(HttpStatusCode.Created, (await AddAsync(owner, part, v.Gol10)).StatusCode);
        var added = await AddAsync(owner, part, v.Gol16, 2011, 2012);

        var view = (await added.Content.ReadFromJsonAsync<PartDto>())!;
        Assert.Equal([("Volkswagen", "Gol", "1.0 8V Flex (G5)", 2009, (int?)2012, false), ("Volkswagen", "Gol", "1.6 8V Flex (G5)", 2011, 2012, true)],
            view.Compatibilities.Select(c => (c.Brand, c.Model, c.Engine, c.YearFrom, c.YearTo, c.Narrowed)));
    }

    [Fact]
    public async Task Regras_da_compatibilidade_viram_mensagens()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var v = await VehiclesAsync(owner);
        var part = await PartAsync(owner, "Farol", active: false);
        await AddAsync(owner, part, v.Gol10);

        var outOfRange = await AddAsync(owner, part, v.Gol16, 2008, 2010);
        Assert.Equal((HttpStatusCode.BadRequest, "Os anos precisam estar dentro da versão (2009–2012)."), (outOfRange.StatusCode, await TitleAsync(outOfRange)));
        Assert.Equal(HttpStatusCode.Conflict, (await AddAsync(owner, part, v.Gol10, 2009, 2012)).StatusCode); // = versão inteira, já existe
        Assert.Equal(HttpStatusCode.NotFound, (await AddAsync(owner, part, Guid.NewGuid())).StatusCode);
    }

    [Fact]
    public async Task Remover_compatibilidade()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var v = await VehiclesAsync(owner);
        var part = await PartAsync(owner, "Farol", active: false);
        var compatibility = (await (await AddAsync(owner, part, v.Gol10)).Content.ReadFromJsonAsync<PartDto>())!.Compatibilities.Single();

        var removed = await SendAsync(api.PanelClient(), owner, HttpMethod.Delete, $"/api/painel/pecas/{part}/compatibilidades/{compatibility.Id}");

        Assert.Empty((await removed.Content.ReadFromJsonAsync<PartDto>())!.Compatibilities);
    }

    [Fact]
    public async Task Loja_publica_busca_por_modelo_versao_e_ano_respeitando_a_restricao_de_anos()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var v = await VehiclesAsync(owner);
        var farol = await PartAsync(owner, "Farol Gol (toda a G5 1.0)");
        var lanterna = await PartAsync(owner, "Lanterna Gol 1.6 só 2011–2012");
        var retrovisor = await PartAsync(owner, "Retrovisor Fox");
        var rascunho = await PartAsync(owner, "Peça em rascunho", active: false);
        await AddAsync(owner, farol, v.Gol10);
        await AddAsync(owner, lanterna, v.Gol16, 2011, 2012);
        await AddAsync(owner, retrovisor, v.Fox10);
        await AddAsync(owner, rascunho, v.Gol10);

        Assert.Equal(new[] { farol, lanterna }.Order(), (await StoreSearchAsync(tenant, $"modelo={v.Gol}")).Order());
        Assert.Equal([farol], await StoreSearchAsync(tenant, $"modelo={v.Gol}&ano=2010"));
        Assert.Equal(new[] { farol, lanterna }.Order(), (await StoreSearchAsync(tenant, $"modelo={v.Gol}&ano=2012")).Order());
        Assert.Equal([lanterna], await StoreSearchAsync(tenant, $"modelo={v.Gol}&versao={v.Gol16}"));
        Assert.Empty(await StoreSearchAsync(tenant, $"modelo={v.Gol}&ano=2013"));
        Assert.Equal([retrovisor], await StoreSearchAsync(tenant, $"modelo={v.Fox}&ano=2006"));
        Assert.Equal(new[] { farol, lanterna, retrovisor }.Order(), (await StoreSearchAsync(tenant, "")).Order()); // sem veículo: todas as ativas
    }

    [Fact]
    public async Task Busca_da_loja_e_o_painel_nao_misturam_lojas()
    {
        var (a, b) = (await api.CreateTenantAsync(), await api.CreateTenantAsync());
        var (ownerA, ownerB) = (await api.SessionForAsync(a), await api.SessionForAsync(b));
        var v = await VehiclesAsync(ownerA);
        var partA = await PartAsync(ownerA, "Farol da loja A");
        await AddAsync(ownerA, partA, v.Gol10);

        Assert.Empty(await StoreSearchAsync(b, $"modelo={v.Gol}"));
        var panelB = await (await GetAsync(api.PanelClient(), ownerB, $"/api/painel/pecas?modelo={v.Gol}")).Content.ReadFromJsonAsync<PageDto>();
        Assert.Equal(0, panelB!.Total);
        Assert.Equal(HttpStatusCode.NotFound, (await AddAsync(ownerB, partA, v.Gol16)).StatusCode);
        var compatibility = (await (await GetAsync(api.PanelClient(), ownerA, $"/api/painel/pecas/{partA}")).Content.ReadFromJsonAsync<PartDto>())!.Compatibilities.Single();
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(api.PanelClient(), ownerB, HttpMethod.Delete, $"/api/painel/pecas/{partA}/compatibilidades/{compatibility.Id}")).StatusCode);
    }

    [Fact]
    public async Task Painel_filtra_pecas_por_veiculo()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var v = await VehiclesAsync(owner);
        var gol = await PartAsync(owner, "Peça do Gol", active: false);
        await PartAsync(owner, "Peça sem compatibilidade", active: false);
        await AddAsync(owner, gol, v.Gol10, 2011, 2012);

        var page = await (await GetAsync(api.PanelClient(), owner, $"/api/painel/pecas?modelo={v.Gol}&ano=2012")).Content.ReadFromJsonAsync<PageDto>();
        Assert.Equal([gol], page!.Items.Select(p => p.Id));
        var outside = await (await GetAsync(api.PanelClient(), owner, $"/api/painel/pecas?modelo={v.Gol}&ano=2009")).Content.ReadFromJsonAsync<PageDto>();
        Assert.Equal(0, outside!.Total);
    }

    [Fact]
    public async Task Versao_descontinuada_some_da_selecao_mas_compatibilidade_existente_continua()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var v = await VehiclesAsync(owner);
        var part = await PartAsync(owner, "Peça do Fox");
        await AddAsync(owner, part, v.Fox10);

        await api.ImportVehiclesAsync(Vehicles.Replace("Volkswagen;Fox;1.0 8V Flex;2004;2009\n", "", StringComparison.Ordinal));

        var versions = await (await GetAsync(api.PanelClient(), owner, $"/api/painel/veiculos/modelos/{v.Fox}/versoes")).Content.ReadFromJsonAsync<List<VersionOption>>();
        Assert.Empty(versions!);
        Assert.True((await (await GetAsync(api.PanelClient(), owner, $"/api/painel/pecas/{part}")).Content.ReadFromJsonAsync<PartDto>())!.Compatibilities.Single().Discontinued);
        Assert.Equal([part], await StoreSearchAsync(tenant, $"modelo={v.Fox}&ano=2005"));
        Assert.Equal(HttpStatusCode.BadRequest, (await AddAsync(owner, await PartAsync(owner, "Outra", active: false), v.Fox10)).StatusCode);
    }
}
