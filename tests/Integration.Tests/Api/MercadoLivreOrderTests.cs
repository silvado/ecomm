using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Ecommerce.Domain.Channels;
using Ecommerce.Domain.Inventory;
using Ecommerce.Domain.Orders;
using Ecommerce.Domain.Payments;
using Ecommerce.Infrastructure.Channels.MercadoLivre;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Ecommerce.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF21/RF17 CA2 — vendas do Mercado Livre no painel (ML simulado, webhook real): pedido unificado com baixa atômica,
/// aviso repetido não duplica, peça sem saldo vira conflito de estoque em análise (Q01, sem cancelar sozinho), cancelado no
/// ML devolve as peças, e venda de anúncio que não é da plataforma é ignorada.
/// </summary>
public sealed class MercadoLivreOrderTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private const string Webhook = "/api/webhooks/canais/" + SalesChannel.MercadoLivre;
    private static readonly string[] Oem = ["5U0941015"];

    private sealed record StartDto(string AuthorizationUrl);
    private sealed record IdDto(Guid Id);
    private sealed record ListingDto(string Channel, string? ExternalId);
    private sealed record PublishedDto(List<ListingDto> Listings);

    private static string NewId() => Random.Shared.NextInt64(100_000_000, 999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private sealed record Shop(Guid TenantId, string Owner, string Seller);

    private async Task<Shop> ShopAsync()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var seller = NewId();
        var start = (await (await SendAsync(api.PanelClient(), owner, HttpMethod.Post, "/api/painel/canais/mercado-livre/conectar")).Content.ReadFromJsonAsync<StartDto>())!;
        var state = Uri.UnescapeDataString(start.AuthorizationUrl.Split("state=")[1].Split('&')[0]);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(api.PanelClient(), owner, HttpMethod.Post, "/api/painel/canais/mercado-livre/retorno",
            new { code = FakeMercadoLivreAuth.CodePrefix + seller, state })).StatusCode);
        return new Shop(tenant.Id, owner, seller);
    }

    /// <summary>Peça ativa anunciada no ML; devolve o id da peça e do anúncio.</summary>
    private async Task<(Guid PartId, string ItemId, string Code)> ListedPartAsync(Shop shop, int quantity = 2)
    {
        var code = "ML-" + NewId()[..6];
        var created = await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Post, "/api/painel/pecas", new
        {
            internalCode = code, title = "Farol dianteiro esquerdo Gol", condition = "used", price = 350m, quantity, oemCodes = Oem,
        });
        var partId = (await created.Content.ReadFromJsonAsync<IdDto>())!.Id;
        using var photo = new HttpRequestMessage(HttpMethod.Post, $"/api/painel/pecas/{partId}/fotos") { Content = new ByteArrayContent(TestImages.Jpeg(800, 600)) };
        photo.Headers.Authorization = new AuthenticationHeaderValue("Bearer", shop.Owner);
        Assert.Equal(HttpStatusCode.Created, (await api.PanelClient().SendAsync(photo)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Put, $"/api/painel/pecas/{partId}/situacao", new { status = "active" })).StatusCode);
        var published = await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Post, $"/api/painel/pecas/{partId}/mercado-livre/anunciar", new
        {
            categoryId = FakeMercadoLivreApi.HeadlightCategory, listingTypeId = "gold_special",
            attributes = new[]
            {
                new { id = "BRAND", valueId = (string?)null, valueName = (string?)"VW" },
                new { id = "ITEM_CONDITION", valueId = (string?)"2230581", valueName = (string?)null },
                new { id = "PART_NUMBER", valueId = (string?)null, valueName = (string?)"5U0941015" },
            },
        });
        Assert.Equal(HttpStatusCode.Created, published.StatusCode);
        var itemId = (await published.Content.ReadFromJsonAsync<PublishedDto>())!.Listings.Single(l => l.Channel == SalesChannel.MercadoLivre).ExternalId!;
        return (partId, itemId, code);
    }

    private Task<HttpResponseMessage> NotifyAsync(string seller, string orderId, string received) =>
        api.ClientFor("api.plataforma.test", $"198.51.109.{Random.Shared.Next(1, 250)}").PostAsync(Webhook, new StringContent($$"""
            {"resource":"/orders/{{orderId}}","user_id":{{seller}},"topic":"orders_v2","application_id":5503910054141466,"attempts":1,
             "sent":"{{received}}","received":"{{received}}"}
            """, Encoding.UTF8, "application/json"));

    private async Task<T> InTenantAsync<T>(Guid tenantId, Func<TenantDbContext, Task<T>> query)
    {
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        return await query(scope.ServiceProvider.GetRequiredService<TenantDbContext>());
    }

    private async Task<CustomerOrder> OrderAsync(Guid tenantId, string orderId, Func<CustomerOrder, bool>? done = null)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var order = await InTenantAsync(tenantId, db => db.CustomerOrders.AsNoTracking().Include(o => o.Items)
                .SingleOrDefaultAsync(o => o.Origin == OrderOrigin.MercadoLivre && o.ExternalId == orderId));
            if (order is not null && (done?.Invoke(order) ?? true)) return order;
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) Assert.Fail($"Pedido {orderId} do ML {(order is null ? "não importado" : "em " + order.Status)}.");
            await Task.Delay(100);
        }
    }

    private Task<int> OnHandAsync(Guid tenantId, Guid partId) => InTenantAsync(tenantId, db => db.Stocks.Where(s => s.PartId == partId).Select(s => s.OnHand).SingleAsync());

    [Fact]
    public async Task Venda_paga_no_ML_vira_pedido_unificado_e_baixa_o_estoque_uma_vez()
    {
        var shop = await ShopAsync();
        var (partId, itemId, code) = await ListedPartAsync(shop, quantity: 2);
        var orderId = FakeMercadoLivreApi.Sell(itemId, 1, shop.Seller, 340m);

        // orders_v2 avisa a cada mudança do pedido: três avisos diferentes, um pedido só.
        foreach (var received in new[] { "2026-10-17T12:00:00Z", "2026-10-17T12:00:05Z", "2026-10-17T12:00:09Z" })
            Assert.Equal(HttpStatusCode.OK, (await NotifyAsync(shop.Seller, orderId, received)).StatusCode);

        var order = await OrderAsync(shop.TenantId, orderId);
        Assert.Equal((OrderStatus.Paid, "COMPRADOR_TESTE", 340m, 25.9m, 365.9m), (order.Status, order.BuyerName, order.ItemsTotal, order.ShippingTotal, order.Total));
        var item = Assert.Single(order.Items);
        Assert.Equal((partId, code, 1, 340m), (item.PartId, item.InternalCode, item.Quantity, item.UnitPrice));
        await Task.Delay(1500); // dá tempo de um eventual segundo processamento
        Assert.Equal(1, await InTenantAsync(shop.TenantId, db => db.CustomerOrders.CountAsync(o => o.ExternalId == orderId)));
        Assert.Equal(1, await OnHandAsync(shop.TenantId, partId));
        Assert.Equal(StockMovementReason.DirectSale, await InTenantAsync(shop.TenantId, db => db.StockMovements
            .Where(m => m.PartId == partId && m.Delta < 0).Select(m => m.Reason).SingleAsync()));

        // O pedido aparece no painel com a origem Mercado Livre.
        var listing = await (await GetAsync(api.PanelClient(), shop.Owner, $"/api/painel/pedidos/{order.Id}")).Content.ReadAsStringAsync();
        Assert.Contains("\"origin\":\"mercadoLivre\"", listing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sem_saldo_vira_conflito_de_estoque_em_analise_sem_cancelar_sozinho()
    {
        var shop = await ShopAsync();
        var (partId, itemId, code) = await ListedPartAsync(shop, quantity: 1);
        // A última unidade saiu no balcão antes de o aviso do ML chegar.
        await InTenantAsync(shop.TenantId, db => db.Database.ExecuteSqlAsync($"UPDATE stocks SET on_hand = 0 WHERE part_id = {partId}"));
        var orderId = FakeMercadoLivreApi.Sell(itemId, 1, shop.Seller);

        await NotifyAsync(shop.Seller, orderId, "2026-10-17T13:00:00Z");

        var order = await OrderAsync(shop.TenantId, orderId);
        Assert.Equal(OrderStatus.NeedsReview, order.Status);
        Assert.StartsWith($"Conflito de estoque: {code} sem saldo", order.ReviewReason, StringComparison.Ordinal);
        Assert.Equal(0, await OnHandAsync(shop.TenantId, partId)); // não baixou o que não havia
    }

    [Fact]
    public async Task Cancelada_no_ML_cancela_aqui_e_devolve_a_peca()
    {
        var shop = await ShopAsync();
        var (partId, itemId, _) = await ListedPartAsync(shop, quantity: 2);
        var orderId = FakeMercadoLivreApi.Sell(itemId, 2, shop.Seller);
        await NotifyAsync(shop.Seller, orderId, "2026-10-17T14:00:00Z");
        await OrderAsync(shop.TenantId, orderId);
        Assert.Equal(0, await OnHandAsync(shop.TenantId, partId));

        FakeMercadoLivreApi.CancelOrder(orderId);
        await NotifyAsync(shop.Seller, orderId, "2026-10-17T14:10:00Z");

        var canceled = await OrderAsync(shop.TenantId, orderId, o => o.Status == OrderStatus.Canceled);
        Assert.Equal("Cancelado no Mercado Livre.", canceled.CancelReason);
        Assert.Equal(2, await OnHandAsync(shop.TenantId, partId));
    }

    [Fact]
    public async Task Venda_de_anuncio_que_nao_e_da_plataforma_e_ignorada()
    {
        var shop = await ShopAsync();
        var orderId = FakeMercadoLivreApi.Sell("MLB" + NewId(), 1, shop.Seller);

        await NotifyAsync(shop.Seller, orderId, "2026-10-17T15:00:00Z");

        var watch = Stopwatch.StartNew();
        while (await InTenantAsync(shop.TenantId, db => db.ExternalEvents.AnyAsync(e => e.ResourceId == $"/orders/{orderId}" && e.Status != ExternalEventStatus.Received)) is false)
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) Assert.Fail("Aviso não processado.");
            await Task.Delay(100);
        }
        await Task.Delay(1000);
        Assert.Equal(0, await InTenantAsync(shop.TenantId, db => db.CustomerOrders.CountAsync(o => o.ExternalId == orderId)));
    }
}
