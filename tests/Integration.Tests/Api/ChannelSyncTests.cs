using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Ecommerce.Application.Channels;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Channels;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF24/RF25 — propagação de estoque para os anúncios: zerou pausa, voltou reativa o que o sistema pausou, pausa do
/// lojista é respeitada, falha do canal tenta de novo, peça inativa pausa, e cada loja só vê os próprios anúncios.
/// </summary>
public sealed class ChannelSyncTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record ListingDto(Guid Id, string Channel, string? ExternalId, string Status, int? LastPushedQuantity, decimal? LastPushedPrice, string? LastError);
    private sealed record ProblemDto(string Title);
    private sealed record LogDto(string Operation, bool Success);
    private sealed record LogPageDto(List<LogDto> Items);

    private sealed record Shop(Tenant Tenant, string Owner);

    private async Task<Shop> ShopAsync()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var connected = await SendAsync(api.PanelClient(), owner, HttpMethod.Post, "/api/painel/canal-simulado/conectar", new { conta = "vend-" + Guid.NewGuid().ToString("N")[..10] });
        Assert.Equal(HttpStatusCode.OK, connected.StatusCode);
        return new Shop(tenant, owner);
    }

    private async Task<(Guid Part, ListingDto Listing)> PublishedPartAsync(Shop shop, int quantity = 2, decimal price = 100m)
    {
        var part = await api.CreatePartAsync(shop.Owner, price, quantity);
        var published = await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Post, "/api/painel/canal-simulado/publicar", new { pecaId = part });
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        return (part, Assert.Single((await published.Content.ReadFromJsonAsync<List<ListingDto>>())!));
    }

    private Task<HttpResponseMessage> SetQuantityAsync(Shop shop, Guid part, int quantity, decimal price = 100m) =>
        SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Put, $"/api/painel/pecas/{part}", new
        {
            title = "Peça de teste", condition = "used", price, quantity, lengthCm = 30, widthCm = 20, heightCm = 10, weightG = 1500, oemCodes = Array.Empty<string>(),
        });

    private async Task<ListingDto> WaitAsync(Shop shop, Guid part, Func<ListingDto, bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var listing = Assert.Single((await (await GetAsync(api.PanelClient(), shop.Owner, $"/api/painel/pecas/{part}/anuncios")).Content.ReadFromJsonAsync<List<ListingDto>>())!);
            if (done(listing)) return listing;
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) Assert.Fail($"Anúncio ainda {listing.Status} com {listing.LastPushedQuantity} un.");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Zerou_pausa_no_canal_e_voltou_reativa_com_a_quantidade_nova()
    {
        var shop = await ShopAsync();
        var (part, listing) = await PublishedPartAsync(shop);
        Assert.Equal(("active", 2), (listing.Status, listing.LastPushedQuantity));

        await SetQuantityAsync(shop, part, 0);
        await WaitAsync(shop, part, l => l.Status == "pausedBySystem");
        Assert.Equal(RemoteListingStatus.Paused, FakeChannelConnector.Remote(listing.ExternalId!)!.Status);

        await SetQuantityAsync(shop, part, 3, 90m);
        var resumed = await WaitAsync(shop, part, l => l is { Status: "active", LastPushedQuantity: 3 });
        Assert.Equal(90m, resumed.LastPushedPrice);
        var remote = FakeChannelConnector.Remote(listing.ExternalId!)!;
        Assert.Equal((RemoteListingStatus.Active, 3, 90m), (remote.Status, remote.Quantity, remote.Price));

        var (count, _, _) = api.Services.GetRequiredService<ChannelSyncMetrics>().Snapshot();
        Assert.True(count >= 2);
    }

    [Fact]
    public async Task Pausa_do_lojista_nao_e_desfeita_pelo_estoque_e_retomar_volta_a_seguir()
    {
        var shop = await ShopAsync();
        var (part, listing) = await PublishedPartAsync(shop);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Post, $"/api/painel/anuncios/{listing.Id}/pausar")).StatusCode);
        Assert.Equal(RemoteListingStatus.Paused, FakeChannelConnector.Remote(listing.ExternalId!)!.Status);

        await SetQuantityAsync(shop, part, 5);
        await Task.Delay(1500);
        Assert.Equal("pausedByUser", (await WaitAsync(shop, part, _ => true)).Status);
        Assert.Equal(RemoteListingStatus.Paused, FakeChannelConnector.Remote(listing.ExternalId!)!.Status);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Post, $"/api/painel/anuncios/{listing.Id}/retomar")).StatusCode);
        var back = await WaitAsync(shop, part, l => l.Status == "active");
        Assert.Equal(5, back.LastPushedQuantity);
        Assert.Equal((RemoteListingStatus.Active, 5), (FakeChannelConnector.Remote(listing.ExternalId!)!.Status, FakeChannelConnector.Remote(listing.ExternalId!)!.Quantity));

        var again = await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Post, $"/api/painel/anuncios/{listing.Id}/retomar");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Canal_fora_do_ar_tenta_de_novo_e_registra_no_log()
    {
        var shop = await ShopAsync();
        var (part, listing) = await PublishedPartAsync(shop);
        FakeChannelConnector.FailNext(listing.ExternalId!, 1);

        await SetQuantityAsync(shop, part, 1);
        await WaitAsync(shop, part, l => l.LastPushedQuantity == 1);
        Assert.Equal(1, FakeChannelConnector.Remote(listing.ExternalId!)!.Quantity);

        var log = (await (await GetAsync(api.PanelClient(), shop.Owner, $"/api/painel/integracoes/log?peca={part}")).Content.ReadFromJsonAsync<LogPageDto>())!;
        Assert.Contains(log.Items, l => l.Operation == "atualizar preço e quantidade" && !l.Success);
        Assert.Contains(log.Items, l => l.Operation == "atualizar preço e quantidade" && l.Success);
    }

    [Fact]
    public async Task Peca_inativada_pausa_o_anuncio_e_reservas_do_site_mudam_a_quantidade()
    {
        var shop = await ShopAsync();
        var (part, listing) = await PublishedPartAsync(shop, quantity: 3);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Put, $"/api/painel/pecas/{part}/situacao", new { status = "inactive" })).StatusCode);
        await WaitAsync(shop, part, l => l.Status == "pausedBySystem");

        await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Put, $"/api/painel/pecas/{part}/situacao", new { status = "active" });
        await WaitAsync(shop, part, l => l is { Status: "active", LastPushedQuantity: 3 });

        // Reserva de 1 unidade (como um pedido faria) reduz o que o canal pode vender.
        await using (var scope = api.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantScope>().Set(shop.Tenant.Id);
            var bus = scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>();
            await bus.InvokeForTenantAsync<Ecommerce.Application.Inventory.ReserveStockResult>(shop.Tenant.Id.ToString(),
                new Ecommerce.Application.Inventory.ReserveStock(part, 1, "teste"));
        }
        await WaitAsync(shop, part, l => l.LastPushedQuantity == 2);
        Assert.Equal(2, FakeChannelConnector.Remote(listing.ExternalId!)!.Quantity);
    }

    [Fact]
    public async Task Cada_loja_ve_so_os_proprios_anuncios_e_conta_do_canal_e_de_uma_loja_so()
    {
        var shop = await ShopAsync();
        var other = await ShopAsync();
        var (part, _) = await PublishedPartAsync(shop);

        Assert.Empty((await (await GetAsync(api.PanelClient(), other.Owner, $"/api/painel/pecas/{part}/anuncios")).Content.ReadFromJsonAsync<List<ListingDto>>())!);
        await using (var scope = api.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantScope>().Set(other.Tenant.Id);
            var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
            Assert.False(await db.ChannelListings.IgnoreQueryFilters().AnyAsync());
        }

        var account = await api.WithPlatformAsync(db => db.ChannelAccounts.Where(a => a.TenantId == shop.Tenant.Id).Select(a => a.ExternalAccountId).SingleAsync());
        var taken = await SendAsync(api.PanelClient(), other.Owner, HttpMethod.Post, "/api/painel/canal-simulado/conectar", new { conta = account });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Contains("outra loja", (await taken.Content.ReadFromJsonAsync<ProblemDto>())!.Title, StringComparison.Ordinal);
    }
}
