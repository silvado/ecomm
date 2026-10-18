using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Ecommerce.Application.Vault;
using Ecommerce.Domain.Channels;
using Ecommerce.Domain.Platform;
using Ecommerce.Domain.Vault;
using Ecommerce.Infrastructure.Channels.Meta;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Ecommerce.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF22 — catálogo do Instagram/Facebook (Graph API simulada): ligar com id do catálogo e token, feed CSV público com
/// segredo (CA1), item atualizado na hora pela Catalog Batch API a cada mudança de estoque ou preço (CA2), link para a
/// peça na loja (CA3), imagem em JPEG, token recusado e desconectar.
/// </summary>
public sealed class MetaCatalogTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private const string Token = "EAAG-token-de-usuario-do-sistema-de-teste";
    private static readonly string[] Oem = ["5U0941015"];

    private sealed record ViewDto(Guid ConnectionId, string CatalogId, string? CatalogName, string FeedUrl);
    private sealed record PhotoDto(Guid Id);
    private sealed record PartDto(Guid Id, List<PhotoDto> Photos);

    private static string NewCatalog() => Random.Shared.NextInt64(100_000_000_000, 999_999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task<(Tenant Tenant, string Owner, string Catalog, ViewDto View)> ConnectedAsync()
    {
        var tenant = await api.CreateTenantAsync(Plan.Professional);
        var owner = await api.SessionForAsync(tenant);
        var catalog = NewCatalog();
        var response = await SendAsync(api.PanelClient(), owner, HttpMethod.Post, "/api/painel/canais/meta/conectar", new { catalogId = catalog, accessToken = Token });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (tenant, owner, catalog, (await response.Content.ReadFromJsonAsync<ViewDto>())!);
    }

    private async Task<PartDto> PartAsync(string token, string title = "Farol dianteiro esquerdo Gol, G5", int quantity = 2, bool photo = true, bool active = true)
    {
        var created = await SendAsync(api.PanelClient(), token, HttpMethod.Post, "/api/painel/pecas", new
        {
            internalCode = "M-" + NewCatalog()[..6], title, description = "Farol usado.\nLente com riscos leves.", condition = "used", price = 350m, quantity,
            oemCodes = Oem,
        });
        var part = (await created.Content.ReadFromJsonAsync<PartDto>())!;
        if (photo)
        {
            using var upload = new HttpRequestMessage(HttpMethod.Post, $"/api/painel/pecas/{part.Id}/fotos") { Content = new ByteArrayContent(TestImages.Jpeg(800, 600)) };
            upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            part = (await (await api.PanelClient().SendAsync(upload)).Content.ReadFromJsonAsync<PartDto>())!;
        }
        if (active)
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(api.PanelClient(), token, HttpMethod.Put, $"/api/painel/pecas/{part.Id}/situacao", new { status = "active" })).StatusCode);
        return part;
    }

    private Task<HttpResponseMessage> FeedAsync(string url) => api.ClientFor("api.plataforma.test").GetAsync(new Uri(url).PathAndQuery);

    private async Task<T> InTenantAsync<T>(Guid tenantId, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        return await action(scope.ServiceProvider);
    }

    private static async Task WaitAsync(Func<bool> done, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) Assert.Fail($"Esperando {what}.");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Feed_publico_lista_as_pecas_ativas_com_foto_no_formato_da_Meta()
    {
        var (tenant, owner, _, view) = await ConnectedAsync();
        Assert.Equal("Catálogo de teste", view.CatalogName);
        Assert.StartsWith($"https://api.plataforma.test/api/catalogo/{tenant.Id}/", view.FeedUrl, StringComparison.Ordinal);
        var part = await PartAsync(owner);
        await PartAsync(owner, "Sem foto", photo: false, active: false);
        await PartAsync(owner, "Rascunho", active: false);

        var response = await FeedAsync(view.FeedUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        var lines = (await response.Content.ReadAsStringAsync()).TrimEnd('\n').Split('\n');
        Assert.Equal("id,title,description,availability,condition,price,link,image_link,additional_image_link,brand,quantity_to_sell_on_facebook", lines[0]);
        var row = Assert.Single(lines.Skip(1));
        Assert.Equal($"{part.Id},\"Farol dianteiro esquerdo Gol, G5\",Farol usado. Lente com riscos leves.,in stock,used,350.00 BRL," +
            $"https://{tenant.Slug}.plataforma.test/peca/{part.Id}-farol-dianteiro-esquerdo-gol-g5," +
            $"https://{tenant.Slug}.plataforma.test/api/loja/fotos/{part.Photos[0].Id}/catalogo.jpg,,{tenant.TradeName},2", row);

        // Segredo errado: como se o endereço não existisse.
        Assert.Equal(HttpStatusCode.NotFound, (await FeedAsync(view.FeedUrl.Replace("/api/catalogo/" + tenant.Id + "/", $"/api/catalogo/{tenant.Id}/x", StringComparison.Ordinal))).StatusCode);
        var other = await api.CreateTenantAsync(Plan.Professional);
        Assert.Equal(HttpStatusCode.NotFound, (await FeedAsync(view.FeedUrl.Replace(tenant.Id.ToString(), other.Id.ToString(), StringComparison.Ordinal))).StatusCode);
    }

    [Fact]
    public async Task Imagem_do_catalogo_sai_em_JPEG()
    {
        var (tenant, owner, _, _) = await ConnectedAsync();
        var part = await PartAsync(owner);

        var response = await api.ClientFor($"{tenant.Slug}.plataforma.test").GetAsync($"/api/loja/fotos/{part.Photos[0].Id}/catalogo.jpg");

        Assert.Equal((HttpStatusCode.OK, "image/jpeg"), (response.StatusCode, response.Content.Headers.ContentType!.MediaType));
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal((0xFF, 0xD8), (bytes[0], bytes[1]));
    }

    [Fact]
    public async Task Estoque_e_preco_vao_ao_catalogo_na_hora_pela_Catalog_Batch_API()
    {
        var (_, owner, catalog, _) = await ConnectedAsync();
        var part = await PartAsync(owner, quantity: 2);
        object Edit(int quantity, decimal price) => new
        {
            title = "Farol dianteiro esquerdo Gol, G5", description = "Farol usado.", condition = "used", price, quantity, oemCodes = Oem,
        };

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(api.PanelClient(), owner, HttpMethod.Put, $"/api/painel/pecas/{part.Id}", Edit(0, 350m))).StatusCode);
        await WaitAsync(() => FakeMetaGraphApi.Item(catalog, part.Id.ToString()) is { } i && (string?)i["availability"] == "out of stock", "o item fora de estoque");

        // Só o preço muda: também vai na hora (antes, só a conferência de hora em hora levava).
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(api.PanelClient(), owner, HttpMethod.Put, $"/api/painel/pecas/{part.Id}", Edit(0, 399.9m))).StatusCode);
        await WaitAsync(() => FakeMetaGraphApi.Item(catalog, part.Id.ToString()) is { } i && (string?)i["price"] == "399.90 BRL", "o preço novo no catálogo");

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(api.PanelClient(), owner, HttpMethod.Put, $"/api/painel/pecas/{part.Id}", Edit(3, 399.9m))).StatusCode);
        await WaitAsync(() => FakeMetaGraphApi.Item(catalog, part.Id.ToString()) is { } i && (string?)i["availability"] == "in stock"
            && (int?)i["quantity_to_sell_on_facebook"] == 3, "o item de volta com 3 unidades");
    }

    [Fact]
    public async Task Token_recusado_pela_Meta_pede_nova_autorizacao()
    {
        var (tenant, owner, _, view) = await ConnectedAsync();
        var part = await PartAsync(owner);
        await InTenantAsync(tenant.Id, sp => sp.GetRequiredService<ISecretVault>()
            .SetAsync(SecretKind.ApiKey, MetaCatalogService.TokenSecret, Encoding.UTF8.GetBytes(FakeMetaGraphApi.InvalidToken)));

        await SendAsync(api.PanelClient(), owner, HttpMethod.Put, $"/api/painel/pecas/{part.Id}",
            new { title = "Farol", description = "x", condition = "used", price = 300m, quantity = 1, oemCodes = Oem });

        var watch = Stopwatch.StartNew();
        while (await InTenantAsync(tenant.Id, sp => sp.GetRequiredService<TenantDbContext>().ChannelConnections.AsNoTracking()
            .Where(c => c.Id == view.ConnectionId).Select(c => c.Status).SingleAsync()) != ChannelConnectionStatus.ReauthRequired)
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) Assert.Fail("Conexão não pediu nova autorização.");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Ligar_confere_o_token_o_plano_e_desconectar_desliga_o_feed()
    {
        var essential = await api.CreateTenantAsync(Plan.Essential);
        var essentialOwner = await api.SessionForAsync(essential);
        var notInPlan = await SendAsync(api.PanelClient(), essentialOwner, HttpMethod.Post, "/api/painel/canais/meta/conectar", new { catalogId = NewCatalog(), accessToken = Token });
        Assert.Equal(HttpStatusCode.Conflict, notInPlan.StatusCode);

        var tenant = await api.CreateTenantAsync(Plan.Professional);
        var owner = await api.SessionForAsync(tenant);
        var refused = await SendAsync(api.PanelClient(), owner, HttpMethod.Post, "/api/painel/canais/meta/conectar",
            new { catalogId = NewCatalog(), accessToken = FakeMetaGraphApi.InvalidToken });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("recusou o token", await TitleAsync(refused), StringComparison.Ordinal);

        var (_, connected, _, view) = await ConnectedAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(api.PanelClient(), connected, HttpMethod.Delete, "/api/painel/canais/meta")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await FeedAsync(view.FeedUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await GetAsync(api.PanelClient(), connected, "/api/painel/canais/meta")).StatusCode);
    }
}
