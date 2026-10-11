using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Ecommerce.Application.Orders;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Inventory;
using Ecommerce.Domain.Orders;
using Ecommerce.Domain.Payments;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Payments;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Ecommerce.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF17 — pedidos no painel: filtros, etapas (enviado/entregue/retirado), análise e cancelamento pela loja, com o
/// estoque andando junto e sem efeito duplicado.
/// </summary>
public sealed class OrderManagementTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record PlacedDto(long Number, decimal Total);
    private sealed record QuoteOptionDto(string ServiceId, decimal Price);
    private sealed record QuoteDto(List<QuoteOptionDto> Options);
    private sealed record SummaryDto(Guid Id, long Number, string Origin, string Status, string BuyerName);
    private sealed record PageDto(List<SummaryDto> Items, int Total);
    private sealed record HistoryDto(DateTimeOffset At, string? From, string To, string Actor, string? UserEmail, string? Note);
    private sealed record OrderDto(Guid Id, long Number, string Status, decimal Total, DateTimeOffset? PaidAt, string? ReviewReason,
        string? CancelReason, string? CanceledBy, string? TrackingCode, DateTimeOffset? ShippedAt, DateTimeOffset? DeliveredAt, List<HistoryDto> History);
    private sealed record StoreOrderDto(string Status, string? TrackingCode, DateTimeOffset? ShippedAt, DateTimeOffset? DeliveredAt);
    private sealed record StockDto(int OnHand, int Reserved, int Available);
    private sealed record PartDto(Guid Id, StockDto Stock);

    private sealed record Shop(Tenant Tenant, string Owner, HttpClient Store);
    private sealed record Order(Guid Id, long Number, string Token, decimal Total, string Cpf);

    private static int _ip;

    private async Task<Shop> ShopAsync()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        await api.ConfigureShippingAsync(owner, tenant, "01310-100", pickup: true, "Balcão da loja");
        return new Shop(tenant, owner, api.StoreClient(tenant, $"198.51.103.{Interlocked.Increment(ref _ip) % 250 + 1}"));
    }

    private async Task<Order> OrderAsync(Shop shop, Guid part, decimal price, int quantity = 1, bool pickup = true, string name = "Maria da Silva")
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var items = new[] { new { partId = part, quantity } };
        var freight = 0m;
        if (!pickup)
        {
            var quote = await shop.Store.PostAsJsonAsync("/api/loja/frete", new { cep = "88015-200", items });
            freight = (await quote.Content.ReadFromJsonAsync<QuoteDto>())!.Options.Single(o => o.ServiceId == "fake-economico").Price;
        }
        var buyer = TestBuyers.Buyer() with { Name = name };
        var response = await shop.Store.PostAsJsonAsync("/api/loja/pedidos", new
        {
            token, items, expectedTotal = price * quantity + freight, buyer,
            delivery = pickup ? (object)new { method = "pickup" } : new { method = "shipping", address = TestBuyers.Address(), serviceId = "fake-economico" },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var placed = (await response.Content.ReadFromJsonAsync<PlacedDto>())!;
        var id = await InTenantAsync(shop.Tenant.Id, db => db.CustomerOrders.Where(o => o.Number == placed.Number).Select(o => o.Id).SingleAsync());
        return new Order(id, placed.Number, token, placed.Total, buyer.Cpf);
    }

    private async Task<T> InTenantAsync<T>(Guid tenantId, Func<TenantDbContext, Task<T>> query)
    {
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        return await query(scope.ServiceProvider.GetRequiredService<TenantDbContext>());
    }

    private async Task<OrderDto> ViewAsync(Shop shop, Guid id) =>
        (await (await GetAsync(api.PanelClient(), shop.Owner, $"/api/painel/pedidos/{id}")).Content.ReadFromJsonAsync<OrderDto>())!;

    private async Task<StockDto> StockAsync(Shop shop, Guid part) =>
        (await (await GetAsync(api.PanelClient(), shop.Owner, $"/api/painel/pecas/{part}")).Content.ReadFromJsonAsync<PartDto>())!.Stock;

    private Task<HttpResponseMessage> ActAsync(Shop shop, Guid id, string action, object? body = null, string? session = null) =>
        SendAsync(api.PanelClient(), session ?? shop.Owner, HttpMethod.Post, $"/api/painel/pedidos/{id}/{action}", body ?? new { });

    /// <summary>Pagamento pelo gateway simulado, como o webhook real, e espera o processamento da fila.</summary>
    private async Task<OrderDto> PayAsync(Shop shop, Order order, decimal? amount = null, Func<OrderDto, bool>? until = null)
    {
        var paymentId = FakePaymentGateway.PaymentId(order.Id, PaymentStatus.Approved, amount ?? order.Total);
        var requestId = Guid.NewGuid().ToString();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/pagamentos/fake/{shop.Tenant.Id}?type=payment&data.id={Uri.EscapeDataString(paymentId)}");
        request.Headers.Add("x-request-id", requestId);
        request.Headers.Add(FakePaymentGateway.SignatureHeader, FakePaymentGateway.Sign(paymentId, requestId));
        Assert.Equal(HttpStatusCode.OK, (await api.PanelClient().SendAsync(request)).StatusCode);

        until ??= o => o.Status is "paid" or "needsReview";
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var view = await ViewAsync(shop, order.Id);
            if (until(view)) return view;
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) Assert.Fail($"Pedido ainda {view.Status}.");
            await Task.Delay(100);
        }
    }

    private async Task ExpireAsync(Shop shop, Order order)
    {
        await InTenantAsync(shop.Tenant.Id, async db =>
        {
            await db.Database.ExecuteSqlAsync($"UPDATE customer_orders SET payment_deadline = now() - interval '1 minute' WHERE id = {order.Id}");
            return await db.Database.ExecuteSqlAsync($"UPDATE stock_reservations SET expires_at = now() - interval '1 minute' WHERE reference = {$"pedido {order.Number}"}");
        });
        await using var scope = api.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeForTenantAsync(shop.Tenant.Id.ToString(), new ExpireOrder(order.Id));
    }

    [Fact]
    public async Task Entrega_vai_de_pago_a_enviado_com_rastreio_e_entregue_e_o_comprador_acompanha()
    {
        var shop = await ShopAsync();
        var part = await api.CreatePartAsync(shop.Owner, price: 120m, quantity: 2);
        var order = await OrderAsync(shop, part, 120m, pickup: false);
        await PayAsync(shop, order);

        var shipped = await ActAsync(shop, order.Id, "enviar", new { rastreio = " BR123456789BR " });
        Assert.Equal(HttpStatusCode.OK, shipped.StatusCode);
        var view = (await shipped.Content.ReadFromJsonAsync<OrderDto>())!;
        Assert.Equal(("shipped", "BR123456789BR"), (view.Status, view.TrackingCode));

        var buyerView = await (await shop.Store.PostAsJsonAsync("/api/loja/pedidos/consulta", new { token = order.Token })).Content.ReadFromJsonAsync<StoreOrderDto>();
        Assert.Equal(("shipped", "BR123456789BR"), (buyerView!.Status, buyerView.TrackingCode));

        Assert.Equal(HttpStatusCode.OK, (await ActAsync(shop, order.Id, "entregar")).StatusCode);
        view = await ViewAsync(shop, order.Id);
        Assert.Equal("delivered", view.Status);
        Assert.NotNull(view.DeliveredAt);

        // Linha do tempo: comprador criou, gateway pagou, loja enviou e entregou (com o e-mail de quem fez).
        Assert.Equal([(null, "pendingPayment", "buyer"), ("pendingPayment", "paid", "payment"), ("paid", "shipped", "user"), ("shipped", "delivered", "user")],
            view.History.Select(h => (h.From, h.To, h.Actor)));
        Assert.Equal("Rastreio: BR123456789BR", view.History[2].Note);
        Assert.All(view.History.Where(h => h.Actor == "user"), h => Assert.EndsWith("@pecas.test", h.UserEmail, StringComparison.Ordinal));

        // Entregue não volta nem é cancelado.
        var cancel = await ActAsync(shop, order.Id, "cancelar", new { motivo = "Teste" });
        Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
        Assert.Equal((0, 0), ((await StockAsync(shop, part)).Reserved, (await StockAsync(shop, part)).OnHand - 1));
    }

    [Fact]
    public async Task Retirada_vai_de_pago_a_retirado_e_nao_aceita_envio()
    {
        var shop = await ShopAsync();
        var part = await api.CreatePartAsync(shop.Owner, price: 50m, quantity: 1);
        var order = await OrderAsync(shop, part, 50m);
        await PayAsync(shop, order);

        Assert.Equal(HttpStatusCode.Conflict, (await ActAsync(shop, order.Id, "enviar", new { rastreio = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(shop, order.Id, "retirar")).StatusCode);

        var view = await ViewAsync(shop, order.Id);
        Assert.Equal("pickedUp", view.Status);
        Assert.NotNull(view.DeliveredAt);
    }

    [Fact]
    public async Task Cancelar_pedido_pago_devolve_as_pecas_ao_estoque_uma_vez()
    {
        var shop = await ShopAsync();
        var part = await api.CreatePartAsync(shop.Owner, price: 80m, quantity: 3);
        var order = await OrderAsync(shop, part, 80m, quantity: 2);
        await PayAsync(shop, order);
        Assert.Equal((1, 0), ((await StockAsync(shop, part)).OnHand, (await StockAsync(shop, part)).Reserved));

        Assert.Equal(HttpStatusCode.BadRequest, (await ActAsync(shop, order.Id, "cancelar", new { motivo = "  " })).StatusCode);
        var canceled = await ActAsync(shop, order.Id, "cancelar", new { motivo = "Peça com defeito" });
        Assert.Equal(HttpStatusCode.OK, canceled.StatusCode);
        var view = (await canceled.Content.ReadFromJsonAsync<OrderDto>())!;
        Assert.Equal(("canceled", "store", "Peça com defeito"), (view.Status, view.CanceledBy, view.CancelReason));
        Assert.Equal((3, 0), ((await StockAsync(shop, part)).OnHand, (await StockAsync(shop, part)).Reserved));

        Assert.Equal(HttpStatusCode.Conflict, (await ActAsync(shop, order.Id, "cancelar", new { motivo = "De novo" })).StatusCode);
        Assert.Equal(3, (await StockAsync(shop, part)).OnHand);
        var movements = await InTenantAsync(shop.Tenant.Id, db => db.StockMovements.Where(m => m.PartId == part && m.Reason == StockMovementReason.OrderCanceled).ToListAsync());
        Assert.Equal(2, Assert.Single(movements).Delta);
        Assert.NotNull(movements[0].UserId);
    }

    [Fact]
    public async Task Dois_cancelamentos_ao_mesmo_tempo_devolvem_a_peca_uma_unica_vez()
    {
        var shop = await ShopAsync();
        var part = await api.CreatePartAsync(shop.Owner, price: 70m, quantity: 1);
        var order = await OrderAsync(shop, part, 70m);
        await PayAsync(shop, order);
        var operatorSession = await api.SessionForAsync(shop.Tenant, TenantRole.Operator);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            ActAsync(shop, order.Id, "cancelar", new { motivo = $"Cancelamento {i}" }, i % 2 == 0 ? shop.Owner : operatorSession)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal((1, 0), ((await StockAsync(shop, part)).OnHand, (await StockAsync(shop, part)).Reserved));
        Assert.Single((await ViewAsync(shop, order.Id)).History, h => h.To == "canceled");
    }

    [Fact]
    public async Task Cancelar_antes_do_pagamento_libera_a_reserva_e_pagamento_que_chega_depois_vai_para_analise()
    {
        var shop = await ShopAsync();
        var part = await api.CreatePartAsync(shop.Owner, price: 40m, quantity: 2);
        var order = await OrderAsync(shop, part, 40m);
        Assert.Equal(1, (await StockAsync(shop, part)).Reserved);

        Assert.Equal(HttpStatusCode.OK, (await ActAsync(shop, order.Id, "cancelar", new { motivo = "Cliente ligou desistindo" })).StatusCode);
        Assert.Equal((2, 0), ((await StockAsync(shop, part)).OnHand, (await StockAsync(shop, part)).Reserved));

        var view = await PayAsync(shop, order);
        Assert.Equal(("needsReview", PaymentHandler.CanceledByStoreReason), (view.Status, view.ReviewReason));
        Assert.Equal((2, 0), ((await StockAsync(shop, part)).OnHand, (await StockAsync(shop, part)).Reserved));

        // Cancelar de novo (devolver o dinheiro) não mexe no estoque: nada foi baixado.
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(shop, order.Id, "cancelar", new { motivo = "Estorno feito no gateway" })).StatusCode);
        Assert.Equal((2, 0), ((await StockAsync(shop, part)).OnHand, (await StockAsync(shop, part)).Reserved));
    }

    [Fact]
    public async Task Aprovar_analise_baixa_as_pecas_reservadas()
    {
        var shop = await ShopAsync();
        var part = await api.CreatePartAsync(shop.Owner, price: 90m, quantity: 2);
        var order = await OrderAsync(shop, part, 90m);
        var review = await PayAsync(shop, order, amount: 89.99m);
        Assert.Equal("needsReview", review.Status);
        Assert.Equal((2, 1), ((await StockAsync(shop, part)).OnHand, (await StockAsync(shop, part)).Reserved));

        var approved = await ActAsync(shop, order.Id, "aprovar");
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var view = (await approved.Content.ReadFromJsonAsync<OrderDto>())!;
        Assert.Equal("paid", view.Status);
        Assert.Null(view.ReviewReason);
        Assert.NotNull(view.PaidAt);
        Assert.Equal((1, 0), ((await StockAsync(shop, part)).OnHand, (await StockAsync(shop, part)).Reserved));
        Assert.Equal(HttpStatusCode.Conflict, (await ActAsync(shop, order.Id, "aprovar")).StatusCode);
    }

    [Fact]
    public async Task Aprovar_analise_sem_estoque_e_recusado_sem_mexer_em_nada()
    {
        var shop = await ShopAsync();
        var part = await api.CreatePartAsync(shop.Owner, price: 60m, quantity: 1);
        var late = await OrderAsync(shop, part, 60m);
        await ExpireAsync(shop, late);
        var other = await OrderAsync(shop, part, 60m);
        await PayAsync(shop, other);

        var review = await PayAsync(shop, late);
        Assert.Equal(("needsReview", PaymentHandler.LateSaleReason), (review.Status, review.ReviewReason));

        var approve = await ActAsync(shop, late.Id, "aprovar");
        Assert.Equal(HttpStatusCode.Conflict, approve.StatusCode);
        Assert.Contains("Sem estoque", await TitleAsync(approve), StringComparison.Ordinal);
        Assert.Equal("needsReview", (await ViewAsync(shop, late.Id)).Status);
        Assert.Equal((0, 0), ((await StockAsync(shop, part)).OnHand, (await StockAsync(shop, part)).Reserved));

        Assert.Equal(HttpStatusCode.OK, (await ActAsync(shop, late.Id, "cancelar", new { motivo = "Peça já vendida; valor devolvido" })).StatusCode);
        Assert.Equal(0, (await StockAsync(shop, part)).OnHand);
    }

    [Fact]
    public async Task Lista_filtra_por_situacao_origem_periodo_e_busca()
    {
        var shop = await ShopAsync();
        var part = await api.CreatePartAsync(shop.Owner, price: 30m, quantity: 10);
        var joao = await OrderAsync(shop, part, 30m, name: "João Pereira");
        var ana = await OrderAsync(shop, part, 30m, name: "Ana Souza");
        await PayAsync(shop, ana);
        await ActAsync(shop, joao.Id, "cancelar", new { motivo = "Teste" });

        async Task<List<long>> NumbersAsync(string query) =>
            (await (await GetAsync(api.PanelClient(), shop.Owner, $"/api/painel/pedidos?{query}")).Content.ReadFromJsonAsync<PageDto>())!
                .Items.Select(i => i.Number).Order().ToList();

        Assert.Equal([joao.Number, ana.Number], await NumbersAsync(""));
        Assert.Equal([ana.Number], await NumbersAsync("situacao=paid"));
        Assert.Equal([joao.Number, ana.Number], await NumbersAsync("situacao=paid,Canceled"));
        Assert.Equal([joao.Number, ana.Number], await NumbersAsync("origem=site"));
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo")).DateTime);
        Assert.Equal([joao.Number, ana.Number], await NumbersAsync($"de={today:yyyy-MM-dd}&ate={today:yyyy-MM-dd}"));
        Assert.Empty(await NumbersAsync($"de={today.AddDays(1):yyyy-MM-dd}"));
        Assert.Empty(await NumbersAsync($"ate={today.AddDays(-1):yyyy-MM-dd}"));
        Assert.Equal([joao.Number], await NumbersAsync("busca=jo%C3%A3o"));
        Assert.Equal([ana.Number], await NumbersAsync($"busca=%23{ana.Number}"));
        Assert.Equal([ana.Number], await NumbersAsync($"busca={ana.Cpf[..3]}.{ana.Cpf[3..6]}.{ana.Cpf[6..9]}-{ana.Cpf[9..]}"));
        Assert.Empty(await NumbersAsync("busca=%25"));

        foreach (var invalid in new[] { "situacao=xyz", "situacao=1", "origem=balcao", "de=11/10/2026", "de=2026-10-12&ate=2026-10-11" })
            Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(api.PanelClient(), shop.Owner, $"/api/painel/pedidos?{invalid}")).StatusCode);
    }

    [Fact]
    public async Task Outra_loja_nao_ve_nem_mexe_no_pedido_e_quem_nao_entrou_nao_tem_acesso()
    {
        var shop = await ShopAsync();
        var other = await ShopAsync();
        var part = await api.CreatePartAsync(shop.Owner, price: 25m, quantity: 1);
        var order = await OrderAsync(shop, part, 25m);

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(api.PanelClient(), other.Owner, $"/api/painel/pedidos/{order.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ActAsync(other, order.Id, "cancelar", new { motivo = "Invasão" })).StatusCode);
        Assert.Empty((await (await GetAsync(api.PanelClient(), other.Owner, $"/api/painel/pedidos?busca={order.Number}")).Content.ReadFromJsonAsync<PageDto>())!.Items);
        var anonymous = await api.PanelClient().PostAsJsonAsync($"/api/painel/pedidos/{order.Id}/cancelar", new { motivo = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        Assert.Equal("pendingPayment", (await ViewAsync(shop, order.Id)).Status);
        Assert.Equal(1, (await StockAsync(shop, part)).Reserved);
    }

    [Fact]
    public async Task Pedido_externo_e_unico_por_loja_canal_e_id()
    {
        var shop = await ShopAsync();
        var part = await api.CreatePartAsync(shop.Owner, price: 25m, quantity: 3);
        var first = await OrderAsync(shop, part, 25m);
        var second = await OrderAsync(shop, part, 25m);

        await InTenantAsync(shop.Tenant.Id, db => db.Database.ExecuteSqlAsync($"UPDATE customer_orders SET external_id = 'ML-1' WHERE id = {first.Id}"));
        var duplicate = await Assert.ThrowsAnyAsync<Exception>(() =>
            InTenantAsync(shop.Tenant.Id, db => db.Database.ExecuteSqlAsync($"UPDATE customer_orders SET external_id = 'ML-1' WHERE id = {second.Id}")));
        Assert.Contains("ix_customer_orders_tenant_id_origin_external_id", duplicate.ToString(), StringComparison.Ordinal);
    }
}
