using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ecommerce.Integration.Tests.Infrastructure;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>RF13 — catálogo da loja pública: busca, página da peça, fotos e peças sem estoque.</summary>
public sealed class StorefrontCatalogTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record SummaryDto(Guid Id, string Title, int Available, Guid? CoverPhotoId);
    private sealed record PageDto(List<SummaryDto> Items, int Total);
    private sealed record CompatDto(string Brand, string Model, string Engine, int YearFrom, int? YearTo);
    private sealed record PartDto(Guid Id, string InternalCode, string Title, string Condition, decimal Price, int Available,
        List<Guid> PhotoIds, List<string> OemCodes, List<CompatDto> Compatibilities);
    private sealed record PanelPartDto(Guid Id, List<PhotoRef> Photos);
    private sealed record PhotoRef(Guid Id);

    private async Task<(Guid Id, Guid? PhotoId)> PartAsync(string token, string title, string? code = null, int quantity = 1,
        bool active = true, string[]? oem = null)
    {
        var created = await SendAsync(api.PanelClient(), token, HttpMethod.Post, "/api/painel/pecas", new
        {
            internalCode = code ?? "V-" + Guid.NewGuid().ToString("N")[..8], title, description = "Descrição.", condition = "used",
            price = 150m, quantity, oemCodes = oem ?? Array.Empty<string>(),
        });
        var id = (await created.Content.ReadFromJsonAsync<PanelPartDto>())!.Id;
        Guid? photoId = null;
        if (active)
        {
            using var photo = new HttpRequestMessage(HttpMethod.Post, $"/api/painel/pecas/{id}/fotos") { Content = new ByteArrayContent(TestImages.Jpeg(160, 120)) };
            photo.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            photoId = (await (await api.PanelClient().SendAsync(photo)).Content.ReadFromJsonAsync<PanelPartDto>())!.Photos.Single().Id;
            await SendAsync(api.PanelClient(), token, HttpMethod.Put, $"/api/painel/pecas/{id}/situacao", new { status = "active" });
        }
        return (id, photoId);
    }

    private static HttpClient Store(ApiFactory api, Ecommerce.Domain.Platform.Tenant tenant) => api.ClientFor($"{tenant.Slug}.plataforma.test");

    private async Task<List<Guid>> SearchAsync(Ecommerce.Domain.Platform.Tenant tenant, string query) =>
        (await Store(api, tenant).GetFromJsonAsync<PageDto>("/api/loja/pecas?" + query))!.Items.Select(i => i.Id).ToList();

    [Fact]
    public async Task Busca_por_texto_ignora_acentos_e_maiusculas_e_acha_codigo_e_oem()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var cambio = await PartAsync(owner, "Câmbio automático Corolla", code: "CAM-77");
        var farol = await PartAsync(owner, "Farol Gol", oem: ["5U0 941 015"]);

        Assert.Equal([cambio.Id], await SearchAsync(tenant, "busca=cambio%20automatico"));
        Assert.Equal([cambio.Id], await SearchAsync(tenant, "busca=C%C3%82MBIO"));
        Assert.Equal([cambio.Id], await SearchAsync(tenant, "busca=cam-77"));
        Assert.Equal([farol.Id], await SearchAsync(tenant, "busca=5u0941015"));
        Assert.Empty(await SearchAsync(tenant, "busca=%25"));
    }

    [Fact]
    public async Task So_pecas_ativas_da_loja_do_host_aparecem()
    {
        var (a, b) = (await api.CreateTenantAsync(), await api.CreateTenantAsync());
        var (ownerA, ownerB) = (await api.SessionForAsync(a), await api.SessionForAsync(b));
        var ativa = await PartAsync(ownerA, "Peça ativa");
        var rascunho = await PartAsync(ownerA, "Peça em rascunho", active: false);
        var inativa = await PartAsync(ownerA, "Peça inativa");
        await SendAsync(api.PanelClient(), ownerA, HttpMethod.Put, $"/api/painel/pecas/{inativa.Id}/situacao", new { status = "inactive" });
        await PartAsync(ownerB, "Peça da loja B");

        Assert.Equal([ativa.Id], await SearchAsync(a, ""));
        Assert.Equal(HttpStatusCode.OK, (await Store(api, a).GetAsync($"/api/loja/pecas/{ativa.Id}")).StatusCode);
        foreach (var hidden in new[] { rascunho.Id, inativa.Id })
            Assert.Equal(HttpStatusCode.NotFound, (await Store(api, a).GetAsync($"/api/loja/pecas/{hidden}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Store(api, b).GetAsync($"/api/loja/pecas/{ativa.Id}")).StatusCode);
    }

    [Fact]
    public async Task Pagina_da_peca_mostra_dados_publicos_e_nada_de_estoque_interno()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var part = await PartAsync(owner, "Farol Gol G5", code: "FAR-1", quantity: 3, oem: ["5U0 941 015"]);

        var response = await Store(api, tenant).GetAsync($"/api/loja/pecas/{part.Id}");
        var json = await response.Content.ReadAsStringAsync();
        var view = (await response.Content.ReadFromJsonAsync<PartDto>())!;

        Assert.Equal(("FAR-1", "used", 150m, 3), (view.InternalCode, view.Condition, view.Price, view.Available));
        Assert.Equal([part.PhotoId!.Value], view.PhotoIds);
        Assert.Equal(["5U0941015"], view.OemCodes);
        foreach (var internalField in new[] { "reserved", "onHand", "status", "weightG", "lengthCm" })
            Assert.DoesNotContain($"\"{internalField}\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Foto_publica_so_de_peca_ativa_da_loja_com_cache_longo()
    {
        var (a, b) = (await api.CreateTenantAsync(), await api.CreateTenantAsync());
        var owner = await api.SessionForAsync(a);
        var part = await PartAsync(owner, "Retrovisor");

        var photo = await Store(api, a).GetAsync($"/api/loja/fotos/{part.PhotoId}/800");
        Assert.Equal(HttpStatusCode.OK, photo.StatusCode);
        Assert.Equal("image/webp", photo.Content.Headers.ContentType!.MediaType);
        Assert.Equal(TimeSpan.FromDays(365), photo.Headers.CacheControl!.MaxAge);
        Assert.Equal(HttpStatusCode.NotFound, (await Store(api, a).GetAsync($"/api/loja/fotos/{part.PhotoId}/1024")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Store(api, b).GetAsync($"/api/loja/fotos/{part.PhotoId}/800")).StatusCode);

        await SendAsync(api.PanelClient(), owner, HttpMethod.Put, $"/api/painel/pecas/{part.Id}/situacao", new { status = "inactive" });
        Assert.Equal(HttpStatusCode.NotFound, (await Store(api, a).GetAsync($"/api/loja/fotos/{part.PhotoId}/800")).StatusCode);
    }

    [Fact]
    public async Task Sem_estoque_aparece_indisponivel_por_padrao_e_some_se_a_loja_ocultar()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var esgotada = await PartAsync(owner, "Peça esgotada", quantity: 0);
        var comEstoque = await PartAsync(owner, "Peça com estoque", quantity: 2);

        var page = await Store(api, tenant).GetFromJsonAsync<PageDto>("/api/loja/pecas");
        Assert.Equal(0, page!.Items.Single(i => i.Id == esgotada.Id).Available);

        var profile = await (await GetAsync(api.PanelClient(), owner, "/api/painel/loja")).Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("False", profile!["hideOutOfStock"].ToString(), ignoreCase: true);
        var saved = await SendAsync(api.PanelClient(), owner, HttpMethod.Put, "/api/painel/loja", new
        {
            tradeName = tenant.TradeName, primaryColor = "#1F5FBF", backgroundColor = "#FFFFFF", textColor = "#1D2330", hideOutOfStock = true,
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        Assert.Equal([comEstoque.Id], await SearchAsync(tenant, ""));
        Assert.Equal(HttpStatusCode.NotFound, (await Store(api, tenant).GetAsync($"/api/loja/pecas/{esgotada.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Store(api, tenant).GetAsync($"/api/loja/fotos/{esgotada.PhotoId}/300")).StatusCode);
    }
}
