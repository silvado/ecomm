using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Ecommerce.Application.Orders;
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

/// <summary>RF15 — pagamento pelo gateway da loja: pago só por webhook validado e consulta ao gateway, sem efeito duplicado.</summary>
public sealed class StorefrontPaymentTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record PlacedDto(long Number, decimal Total);
    private sealed record RedirectDto(string RedirectUrl);
    private sealed record SummaryDto(Guid Id, string Status);
    private sealed record PageDto(List<SummaryDto> Items);
    private sealed record PaymentDto(string Gateway, string ExternalId, string Status, decimal Amount);
    private sealed record OrderDto(Guid Id, long Number, string Status, decimal Total, DateTimeOffset? PaidAt, string? ReviewReason, List<PaymentDto> Payments);
    private sealed record StockDto(int OnHand, int Reserved, int Available);
    private sealed record PartDto(Guid Id, StockDto Stock);

    private sealed record Shop(Tenant Tenant, string Owner, HttpClient Store);

    private sealed record Order(Guid Id, string Token, decimal Total);

    private static int _ip;

    private async Task<Shop> ShopAsync()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        await api.ConfigureShippingAsync(owner, tenant, "01310-100", pickup: true, "Balcão");
        return new Shop(tenant, owner, api.StoreClient(tenant, $"198.51.101.{Interlocked.Increment(ref _ip) % 250 + 1}"));
    }

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Pedido com retirada (sem frete), pela vitrine.</summary>
    private async Task<Order> OrderAsync(Shop shop, params (Guid Part, int Quantity, decimal Price)[] items)
    {
        var token = NewToken();
        var total = items.Sum(i => i.Quantity * i.Price);
        var response = await shop.Store.PostAsJsonAsync("/api/loja/pedidos", new
        {
            token, expectedTotal = total, buyer = TestBuyers.Buyer(),
            items = items.Select(i => new { partId = i.Part, quantity = i.Quantity }),
            delivery = new { method = "pickup" },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var number = (await response.Content.ReadFromJsonAsync<PlacedDto>())!.Number;
        var page = (await (await GetAsync(api.PanelClient(), shop.Owner, "/api/painel/pedidos")).Content.ReadFromJsonAsync<PageDto>())!;
        var id = await InTenantAsync(shop.Tenant.Id, db => db.CustomerOrders.Where(o => o.Number == number).Select(o => o.Id).SingleAsync());
        Assert.Contains(page.Items, o => o.Id == id);
        return new Order(id, token, total);
    }

    private async Task<T> InTenantAsync<T>(Guid tenantId, Func<TenantDbContext, Task<T>> query)
    {
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        return await query(scope.ServiceProvider.GetRequiredService<TenantDbContext>());
    }

    private async Task<OrderDto> OrderViewAsync(Shop shop, Guid id) =>
        (await (await GetAsync(api.PanelClient(), shop.Owner, $"/api/painel/pedidos/{id}")).Content.ReadFromJsonAsync<OrderDto>())!;

    /// <summary>O processamento é pela fila: espera o pedido chegar ao estado esperado.</summary>
    private async Task<OrderDto> WaitForAsync(Shop shop, Guid id, Func<OrderDto, bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var order = await OrderViewAsync(shop, id);
            if (done(order)) return order;
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) Assert.Fail($"Pedido ainda {order.Status} com {order.Payments.Count} pagamento(s).");
            await Task.Delay(100);
        }
    }

    private async Task<StockDto> StockAsync(Shop shop, Guid part) =>
        (await (await GetAsync(api.PanelClient(), shop.Owner, $"/api/painel/pecas/{part}")).Content.ReadFromJsonAsync<PartDto>())!.Stock;

    /// <summary>Notificação como o gateway fake envia: assinada com o id do pagamento e o x-request-id.</summary>
    private Task<HttpResponseMessage> NotifyAsync(Guid tenantId, string paymentId, string? requestId = null, string? signature = null,
        string type = "payment", string gateway = FakePaymentGateway.GatewayName)
    {
        requestId ??= Guid.NewGuid().ToString();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/pagamentos/{gateway}/{tenantId}?type={type}&data.id={Uri.EscapeDataString(paymentId)}")
        {
            Content = JsonContent.Create(new { type, data = new { id = paymentId } }),
        };
        request.Headers.Add("x-request-id", requestId);
        request.Headers.Add(FakePaymentGateway.SignatureHeader, signature ?? FakePaymentGateway.Sign(paymentId, requestId));
        return api.PanelClient().SendAsync(request);
    }

    private Task<int> EventCountAsync(Guid tenantId) => InTenantAsync(tenantId, db => db.ExternalEvents.CountAsync());

    private async Task ExpireAsync(Shop shop, Order order)
    {
        await InTenantAsync(shop.Tenant.Id, async db =>
        {
            await db.Database.ExecuteSqlAsync($"UPDATE customer_orders SET payment_deadline = now() - interval '1 minute' WHERE id = {order.Id}");
            return await db.Database.ExecuteSqlAsync($"UPDATE stock_reservations SET expires_at = now() - interval '1 minute'");
        });
        await using var scope = api.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeForTenantAsync(shop.Tenant.Id.ToString(), new ExpireOrder(order.Id));
    }

    [Fact]
    public async Task Comprador_paga_pelo_gateway_e_o_pedido_vira_pago_com_baixa_das_pecas()
    {
        var shop = await ShopAsync();
        var part = await api.CreatePartAsync(shop.Owner, price: 150m, quantity: 2);
        var order = await OrderAsync(shop, (part, 1, 150m));

        var start = await shop.Store.PostAsJsonAsync("/api/loja/pedidos/pagamento", new { token = order.Token });
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var redirect = (await start.Content.ReadFromJsonAsync<RedirectDto>())!.RedirectUrl;
        Assert.StartsWith($"{FakePaymentGateway.SimulationPath}/{shop.Tenant.Id}/{order.Id}?", redirect, StringComparison.Ordinal);

        // Página do "gateway" e aprovação, como o comprador faria; volta para a página do pedido da loja.
        var noRedirect = api.CreateClient(new() { AllowAutoRedirect = false });
        var page = await noRedirect.GetStringAsync(redirect);
        Assert.Contains("R$ 150,00", page, StringComparison.Ordinal);
        var approve = await noRedirect.PostAsync(redirect.Replace("?", "/aprovar?", StringComparison.Ordinal), null);
        Assert.Equal(HttpStatusCode.Redirect, approve.StatusCode);
        Assert.Equal($"http://{shop.Tenant.Slug}.plataforma.test/pedido", approve.Headers.Location!.ToString());

        var paid = await WaitForAsync(shop, order.Id, o => o.Status == "paid");
        Assert.NotNull(paid.PaidAt);
        var payment = Assert.Single(paid.Payments);
        Assert.Equal(("fake", "approved", 150m), (payment.Gateway, payment.Status, payment.Amount));
        var stock = await StockAsync(shop, part);
        Assert.Equal((1, 0, 1), (stock.OnHand, stock.Reserved, stock.Available));

        // Pedido pago não abre outro pagamento.
        var again = await shop.Store.PostAsJsonAsync("/api/loja/pedidos/pagamento", new { token = order.Token });
        Assert.Equal((HttpStatusCode.Conflict, "Este pedido não aguarda mais pagamento."), (again.StatusCode, await TitleAsync(again)));
    }

    [Fact]
    public async Task Notificacao_repetida_ou_de_novo_para_o_mesmo_pagamento_nao_baixa_duas_vezes()
    {
        var shop = await ShopAsync();
        var part = await api.CreatePartAsync(shop.Owner, price: 80m, quantity: 3);
        var order = await OrderAsync(shop, (part, 2, 80m));
        var paymentId = FakePaymentGateway.PaymentId(order.Id, PaymentStatus.Approved, 160m);

        var requestId = Guid.NewGuid().ToString();
        var responses = await Task.WhenAll(NotifyAsync(shop.Tenant.Id, paymentId, requestId), NotifyAsync(shop.Tenant.Id, paymentId, requestId));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        await WaitForAsync(shop, order.Id, o => o.Status == "paid");
        Assert.Equal(1, await EventCountAsync(shop.Tenant.Id));

        // O gateway avisa de novo (outro x-request-id): registra, consulta e não baixa outra vez.
        Assert.Equal(HttpStatusCode.OK, (await NotifyAsync(shop.Tenant.Id, paymentId)).StatusCode);
        var processed = Stopwatch.StartNew();
        while (await InTenantAsync(shop.Tenant.Id, db => db.ExternalEvents.CountAsync(e => e.Status == ExternalEventStatus.Received)) > 0 && processed.Elapsed < TimeSpan.FromSeconds(20))
            await Task.Delay(100);

        var view = await OrderViewAsync(shop, order.Id);
        Assert.Equal(("paid", 1), (view.Status, view.Payments.Count));
        var stock = await StockAsync(shop, part);
        Assert.Equal((1, 0), (stock.OnHand, stock.Reserved));
    }

    [Fact]
    public async Task Webhook_sem_assinatura_valida_ou_de_loja_desconhecida_e_recusado()
    {
        var shop = await ShopAsync();
        var part = await api.CreatePartAsync(shop.Owner, price: 80m);
        var order = await OrderAsync(shop, (part, 1, 80m));
        var paymentId = FakePaymentGateway.PaymentId(order.Id, PaymentStatus.Approved, 80m);

        Assert.Equal(HttpStatusCode.Unauthorized, (await NotifyAsync(shop.Tenant.Id, paymentId, signature: new string('0', 64))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await NotifyAsync(Guid.NewGuid(), paymentId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await NotifyAsync(shop.Tenant.Id, paymentId, gateway: "outro")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await NotifyAsync(shop.Tenant.Id, paymentId, type: "merchant_order")).StatusCode);

        Assert.Equal(0, await EventCountAsync(shop.Tenant.Id));
        Assert.Equal("pendingPayment", (await OrderViewAsync(shop, order.Id)).Status);
    }

    [Fact]
    public async Task Pagamento_recusado_so_fica_registrado()
    {
        var shop = await ShopAsync();
        var part = await api.CreatePartAsync(shop.Owner, price: 80m);
        var order = await OrderAsync(shop, (part, 1, 80m));

        await NotifyAsync(shop.Tenant.Id, FakePaymentGateway.PaymentId(order.Id, PaymentStatus.Rejected, 80m));

        var view = await WaitForAsync(shop, order.Id, o => o.Payments.Count == 1);
        Assert.Equal(("pendingPayment", "rejected"), (view.Status, view.Payments[0].Status));
        Assert.Equal(1, (await StockAsync(shop, part)).Reserved);
    }

    [Fact]
    public async Task Estorno_depois_de_pago_coloca_o_pedido_em_analise()
    {
        var shop = await ShopAsync();
        var part = await api.CreatePartAsync(shop.Owner, price: 80m);
        var order = await OrderAsync(shop, (part, 1, 80m));
        await NotifyAsync(shop.Tenant.Id, FakePaymentGateway.PaymentId(order.Id, PaymentStatus.Approved, 80m));
        await WaitForAsync(shop, order.Id, o => o.Status == "paid");

        await NotifyAsync(shop.Tenant.Id, FakePaymentGateway.PaymentId(order.Id, PaymentStatus.Refunded, 80m));

        var view = await WaitForAsync(shop, order.Id, o => o.Status != "paid");
        Assert.Equal(("needsReview", PaymentHandler.RefundedReason), (view.Status, view.ReviewReason));
    }

    [Fact]
    public async Task Valor_pago_diferente_do_total_fica_em_analise_sem_baixa()
    {
        var shop = await ShopAsync();
        var part = await api.CreatePartAsync(shop.Owner, price: 80m);
        var order = await OrderAsync(shop, (part, 1, 80m));

        await NotifyAsync(shop.Tenant.Id, FakePaymentGateway.PaymentId(order.Id, PaymentStatus.Approved, 8m));

        var view = await WaitForAsync(shop, order.Id, o => o.Status != "pendingPayment");
        Assert.Equal("needsReview", view.Status);
        Assert.StartsWith("Valor pago (R$ 8.00) diferente do total do pedido (R$ 80.00)", view.ReviewReason, StringComparison.Ordinal);
        Assert.Equal((3, 1), (await StockAsync(shop, part)) is var s ? (s.OnHand, s.Reserved) : default); // segue reservada até a loja resolver
    }

    [Fact]
    public async Task Pagamento_depois_do_prazo_com_pecas_disponiveis_vira_pago()
    {
        var shop = await ShopAsync();
        var part = await api.CreatePartAsync(shop.Owner, price: 80m, quantity: 1);
        var order = await OrderAsync(shop, (part, 1, 80m));
        await ExpireAsync(shop, order);
        Assert.Equal("canceled", (await OrderViewAsync(shop, order.Id)).Status);

        await NotifyAsync(shop.Tenant.Id, FakePaymentGateway.PaymentId(order.Id, PaymentStatus.Approved, 80m));

        await WaitForAsync(shop, order.Id, o => o.Status == "paid");
        Assert.Equal((0, 0), (await StockAsync(shop, part)) is var s ? (s.OnHand, s.Reserved) : default);
    }

    [Fact]
    public async Task Pagamento_depois_do_prazo_com_peca_ja_vendida_fica_em_analise_e_nada_e_baixado()
    {
        var shop = await ShopAsync();
        // Ordem por id: a primeira peça é baixada e precisa ser desfeita quando a segunda falha.
        var ids = new[] { await api.CreatePartAsync(shop.Owner, price: 10m, quantity: 1), await api.CreatePartAsync(shop.Owner, price: 20m, quantity: 1) }.Order().ToArray();
        var (first, last) = (ids[0], ids[1]);
        var order = await OrderAsync(shop, (first, 1, PriceOf(first)), (last, 1, PriceOf(last)));
        await ExpireAsync(shop, order);
        await OrderAsync(shop, (last, 1, PriceOf(last))); // vendida a outro comprador enquanto isso

        await NotifyAsync(shop.Tenant.Id, FakePaymentGateway.PaymentId(order.Id, PaymentStatus.Approved, order.Total));

        var view = await WaitForAsync(shop, order.Id, o => o.Status == "needsReview");
        Assert.Equal(PaymentHandler.LateSaleReason, view.ReviewReason);
        Assert.Equal("approved", Assert.Single(view.Payments).Status);
        Assert.Equal((1, 0, 1), (await StockAsync(shop, first)) is var s ? (s.OnHand, s.Reserved, s.Available) : default);
        Assert.Equal(0, await InTenantAsync(shop.Tenant.Id, db => db.StockMovements.CountAsync(m => m.PartId == first && m.Delta < 0)));

        decimal PriceOf(Guid id) => id == ids[0] ? 10m : 20m;
    }

    [Fact]
    public async Task Pagamento_de_pedido_de_outra_loja_e_ignorado()
    {
        var (a, b) = (await ShopAsync(), await ShopAsync());
        var part = await api.CreatePartAsync(a.Owner, price: 80m);
        var order = await OrderAsync(a, (part, 1, 80m));

        Assert.Equal(HttpStatusCode.OK, (await NotifyAsync(b.Tenant.Id, FakePaymentGateway.PaymentId(order.Id, PaymentStatus.Approved, 80m))).StatusCode);

        var watch = Stopwatch.StartNew();
        while (await InTenantAsync(b.Tenant.Id, db => db.ExternalEvents.CountAsync(e => e.Status == ExternalEventStatus.Ignored)) == 0)
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) Assert.Fail("Notificação não foi processada.");
            await Task.Delay(100);
        }
        Assert.Equal("pendingPayment", (await OrderViewAsync(a, order.Id)).Status);
        Assert.Equal(0, await InTenantAsync(b.Tenant.Id, db => db.Payments.CountAsync()));
    }
}
