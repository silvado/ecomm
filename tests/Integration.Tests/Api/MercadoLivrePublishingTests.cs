using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Ecommerce.Application.Channels;
using Ecommerce.Domain.Channels;
using Ecommerce.Infrastructure.Channels.MercadoLivre;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Ecommerce.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF21 CA3 e RF24 no Mercado Livre (ML simulado, cliente HTTP real): categoria sugerida, formulário com obrigatórios e
/// pré-preenchidos, anúncio no modelo User Products com fotos, garantia e descrição, recusa do ML com o motivo, e o
/// estoque mandando no anúncio (pausa ao zerar, volta com saldo, reconciliação corrige o que mexeram lá).
/// </summary>
public sealed class MercadoLivrePublishingTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record StartDto(string AuthorizationUrl);
    private sealed record IdDto(Guid Id);
    private sealed record ListingDto(Guid Id, string Channel, string? ExternalId, string Status);
    private sealed record CategoryDto(string CategoryId, string CategoryName);
    private sealed record SpecDto(string Id, string Name, bool Required);
    private sealed record ValueDto(string Id, string? ValueId, string? ValueName);
    private sealed record FormDto(List<SpecDto> Attributes, List<ValueDto> Prefilled, List<string> Warnings);

    private static readonly string[] Oem = ["5U0941015"];

    private static string NewId() => Random.Shared.NextInt64(100_000_000, 999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task<(Guid TenantId, string Owner)> ConnectedAsync()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var start = (await (await SendAsync(api.PanelClient(), owner, HttpMethod.Post, "/api/painel/canais/mercado-livre/conectar")).Content.ReadFromJsonAsync<StartDto>())!;
        var state = Uri.UnescapeDataString(start.AuthorizationUrl.Split("state=")[1].Split('&')[0]);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(api.PanelClient(), owner, HttpMethod.Post, "/api/painel/canais/mercado-livre/retorno",
            new { code = FakeMercadoLivreAuth.CodePrefix + NewId(), state })).StatusCode);
        return (tenant.Id, owner);
    }

    /// <summary>Peça ativa, com foto, 2 unidades e um código OEM.</summary>
    private async Task<(Guid PartId, string Code)> PartAsync(string token, string title = "Farol dianteiro esquerdo Gol G5", int quantity = 2)
    {
        var code = "ML-" + NewId()[..6];
        var created = await SendAsync(api.PanelClient(), token, HttpMethod.Post, "/api/painel/pecas", new
        {
            internalCode = code, title, description = "Farol usado, lente com riscos leves.", condition = "used", price = 350m, quantity,
            oemCodes = Oem,
        });
        var id = (await created.Content.ReadFromJsonAsync<IdDto>())!.Id;
        using var photo = new HttpRequestMessage(HttpMethod.Post, $"/api/painel/pecas/{id}/fotos") { Content = new ByteArrayContent(TestImages.Jpeg(2400, 1200)) };
        photo.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Created, (await api.PanelClient().SendAsync(photo)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(api.PanelClient(), token, HttpMethod.Put, $"/api/painel/pecas/{id}/situacao", new { status = "active" })).StatusCode);
        return (id, code);
    }

    private static object Options(string? brand = "VW", string category = FakeMercadoLivreApi.HeadlightCategory, string? warranty = null) => new
    {
        categoryId = category,
        listingTypeId = "gold_special",
        attributes = new[]
        {
            new { id = "BRAND", valueId = (string?)null, valueName = brand },
            new { id = "ITEM_CONDITION", valueId = (string?)"2230581", valueName = (string?)null },
            new { id = "PART_NUMBER", valueId = (string?)null, valueName = (string?)"5U0941015" },
            new { id = "INVENTADO", valueId = (string?)null, valueName = (string?)"some" },
        },
        warrantyType = warranty,
        warrantyTime = warranty is null ? null : "90 dias",
    };

    private async Task<string> PublishAsync(string token, Guid partId)
    {
        var response = await SendAsync(api.PanelClient(), token, HttpMethod.Post, $"/api/painel/pecas/{partId}/mercado-livre/anunciar", Options());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<ListingDto>>())!.Single(l => l.Channel == SalesChannel.MercadoLivre).ExternalId!;
    }

    private async Task<T> InTenantAsync<T>(Guid tenantId, Func<TenantDbContext, Task<T>> query)
    {
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        return await query(scope.ServiceProvider.GetRequiredService<TenantDbContext>());
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
    public async Task Sugere_a_categoria_e_monta_o_formulario_com_obrigatorios_e_pre_preenchidos()
    {
        var (_, owner) = await ConnectedAsync();
        var (partId, code) = await PartAsync(owner);

        var categories = (await (await GetAsync(api.PanelClient(), owner, $"/api/painel/pecas/{partId}/mercado-livre/categorias")).Content.ReadFromJsonAsync<List<CategoryDto>>())!;
        Assert.Equal(FakeMercadoLivreApi.HeadlightCategory, categories[0].CategoryId);

        var form = (await (await GetAsync(api.PanelClient(), owner, $"/api/painel/pecas/{partId}/mercado-livre/formulario?categoria={categories[0].CategoryId}"))
            .Content.ReadFromJsonAsync<FormDto>())!;
        Assert.Equal(["BRAND", "ITEM_CONDITION", "PART_NUMBER"], form.Attributes.Where(a => a.Required).Select(a => a.Id));
        Assert.DoesNotContain(form.Attributes, a => a.Id == "PRODUCT_DATA_SOURCE"); // read_only: a loja não preenche
        Assert.Contains(new ValueDto("ITEM_CONDITION", "2230581", null), form.Prefilled); // Usado
        Assert.Contains(new ValueDto("PART_NUMBER", null, "5U0941015"), form.Prefilled);
        Assert.Contains(new ValueDto("SELLER_SKU", null, code), form.Prefilled);
        Assert.Contains(form.Warnings, w => w.Contains("medidas da embalagem", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.BadRequest,
            (await GetAsync(api.PanelClient(), owner, $"/api/painel/pecas/{partId}/mercado-livre/formulario?categoria=MLB-NAO-EXISTE")).StatusCode);
    }

    [Fact]
    public async Task Publica_no_modelo_User_Products_com_foto_garantia_descricao_e_SKU()
    {
        var (tenantId, owner) = await ConnectedAsync();
        var (partId, code) = await PartAsync(owner);

        var missing = await SendAsync(api.PanelClient(), owner, HttpMethod.Post, $"/api/painel/pecas/{partId}/mercado-livre/anunciar", Options(brand: null));
        Assert.Equal((HttpStatusCode.Conflict, "Preencha os campos obrigatórios do Mercado Livre: Marca."), (missing.StatusCode, await TitleAsync(missing)));

        var itemId = await PublishAsync(owner, partId);

        var item = FakeMercadoLivreApi.Item(itemId)!;
        Assert.Equal(("Farol dianteiro esquerdo Gol G5", FakeMercadoLivreApi.HeadlightCategory, 350m, 2, "BRL", "used", "gold_special"),
            ((string?)item["family_name"], (string?)item["category_id"], (decimal)item["price"]!, (int)item["available_quantity"]!, (string?)item["currency_id"],
                (string?)item["condition"], (string?)item["listing_type_id"]));
        Assert.Single((JsonArray)item["pictures"]!);
        var attributes = ((JsonArray)item["attributes"]!).OfType<JsonObject>().ToDictionary(a => (string)a["id"]!, a => (string?)a["value_name"] ?? (string?)a["value_id"]);
        Assert.Equal(("VW", "2230581", code), (attributes["BRAND"], attributes["ITEM_CONDITION"], attributes["SELLER_SKU"]));
        Assert.False(attributes.ContainsKey("INVENTADO")); // atributo que a categoria não tem não vai
        Assert.Equal("Sem garantia", (string?)item["sale_terms"]![0]!["value_name"]);
        Assert.Equal("Farol usado, lente com riscos leves.", FakeMercadoLivreApi.Description(itemId));

        var listing = await InTenantAsync(tenantId, db => db.ChannelListings.AsNoTracking().SingleAsync(l => l.ExternalId == itemId));
        Assert.Equal(ListingStatus.Active, listing.Status);
        Assert.Contains(FakeMercadoLivreApi.HeadlightCategory, listing.ChannelData, StringComparison.Ordinal);

        var again = await SendAsync(api.PanelClient(), owner, HttpMethod.Post, $"/api/painel/pecas/{partId}/mercado-livre/anunciar", Options());
        Assert.Equal("A peça já está anunciada neste canal.", await TitleAsync(again));
    }

    [Fact]
    public async Task Recusa_do_ML_volta_com_o_motivo_e_nada_fica_gravado()
    {
        var (tenantId, owner) = await ConnectedAsync();
        var (partId, _) = await PartAsync(owner, "Retrovisor " + FakeMercadoLivreApi.RefuseMarker);

        var refused = await SendAsync(api.PanelClient(), owner, HttpMethod.Post, $"/api/painel/pecas/{partId}/mercado-livre/anunciar",
            Options(category: FakeMercadoLivreApi.MirrorCategory));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("O Mercado Livre recusou o anúncio: O título contém palavras não permitidas.", await TitleAsync(refused));
        Assert.Equal(0, await InTenantAsync(tenantId, db => db.ChannelListings.CountAsync()));
    }

    [Fact]
    public async Task Sem_conta_conectada_ou_sem_saldo_nao_anuncia()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var (partId, _) = await PartAsync(owner);
        var notConnected = await GetAsync(api.PanelClient(), owner, $"/api/painel/pecas/{partId}/mercado-livre/categorias");
        Assert.Equal((HttpStatusCode.Conflict, "Conecte a conta do Mercado Livre em Canais de venda antes de anunciar."),
            (notConnected.StatusCode, await TitleAsync(notConnected)));

        var (_, connected) = await ConnectedAsync();
        var (empty, _) = await PartAsync(connected, quantity: 0);
        var noStock = await SendAsync(api.PanelClient(), connected, HttpMethod.Post, $"/api/painel/pecas/{empty}/mercado-livre/anunciar", Options());
        Assert.StartsWith("A peça está sem saldo", await TitleAsync(noStock), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Estoque_manda_no_anuncio_e_a_conferencia_corrige_o_que_mexeram_no_ML()
    {
        var (tenantId, owner) = await ConnectedAsync();
        var (partId, code) = await PartAsync(owner);
        var itemId = await PublishAsync(owner, partId);
        object Request(int quantity) => new
        {
            title = "Farol dianteiro esquerdo Gol G5", description = "Farol usado, lente com riscos leves.", condition = "used", price = 360m, quantity,
            oemCodes = Oem,
        };

        // Zerou: pausa pelo status (peça usada não pausa com quantidade zero no ML).
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(api.PanelClient(), owner, HttpMethod.Put, $"/api/painel/pecas/{partId}", Request(0))).StatusCode);
        await WaitAsync(() => (string?)FakeMercadoLivreApi.Item(itemId)!["status"] == "paused", "o anúncio pausado no ML");

        // Voltou: quantidade e preço novos (o preço mudou durante a pausa e precisa ir junto), e reativa.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(api.PanelClient(), owner, HttpMethod.Put, $"/api/painel/pecas/{partId}", Request(3))).StatusCode);
        await WaitAsync(() => FakeMercadoLivreApi.Item(itemId) is { } i && (string?)i["status"] == "active" && (int)i["available_quantity"]! == 3
            && (decimal)i["price"]! == 360m, "o anúncio ativo com 3 unidades a R$ 360");

        // Alguém baixou o preço direto no ML: a conferência volta ao preço da loja.
        FakeMercadoLivreApi.Change(itemId, price: 1m);
        var listingId = await InTenantAsync(tenantId, db => db.ChannelListings.Where(l => l.ExternalId == itemId).Select(l => l.Id).SingleAsync());
        await api.Services.GetRequiredService<IMessageBus>().InvokeForTenantAsync(tenantId.ToString(), new ReconcileListing(listingId));
        Assert.Equal(360m, (decimal)FakeMercadoLivreApi.Item(itemId)!["price"]!);
    }
}
