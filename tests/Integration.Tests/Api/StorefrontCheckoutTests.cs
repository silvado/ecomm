using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Ecommerce.Application.Orders;
using Ecommerce.Domain.Orders;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Ecommerce.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wolverine;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>RF14 CA3 — checkout como convidado: pedido com as peças reservadas (RF12/RNF02), número por loja e isolamento.</summary>
public sealed class StorefrontCheckoutTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record PlacedDto(long Number, decimal Total, DateTimeOffset PaymentDeadline);
    private sealed record ItemDto(Guid PartId, string Title, decimal UnitPrice, int Quantity);
    private sealed record AddressDto(string PostalCode, string Street, string Number, string? Complement, string District, string City, string State);
    private sealed record StoreOrderDto(long Number, string Status, string BuyerName, string DeliveryMethod, AddressDto? Address, string? Service,
        string? PickupAddress, List<ItemDto> Items, decimal ItemsTotal, decimal ShippingTotal, decimal Total, string? CancelReason);
    private sealed record SummaryDto(Guid Id, long Number, string Status, string BuyerName, string DeliveryMethod, int ItemCount, decimal Total);
    private sealed record PageDto(List<SummaryDto> Items, int Total);
    private sealed record BuyerDto(string Name, string Email, string Phone, string Cpf);
    private sealed record OrderDto(Guid Id, long Number, string Status, BuyerDto Buyer, AddressDto? Address, List<ItemDto> Items, decimal Total);
    private sealed record StockDto(int OnHand, int Reserved, int Available);
    private sealed record PartDto(Guid Id, StockDto Stock);
    private sealed record OptionDto(string ServiceId, decimal Price);
    private sealed record QuoteDto(List<OptionDto> Options);

    private sealed record Shop(Tenant Tenant, string Owner, HttpClient Store);

    private static int _ip;

    /// <summary>IP novo por cliente: o limite de pedidos por IP não vaza de um teste para outro.</summary>
    private static string NextIp() => $"198.51.100.{Interlocked.Increment(ref _ip) % 250 + 1}";

    private async Task<Shop> ShopAsync(bool pickup = true)
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        Assert.Equal(HttpStatusCode.OK, (await api.ConfigureShippingAsync(owner, tenant, "01310-100", pickup, pickup ? "Rua das Peças, 100" : null)).StatusCode);
        return new Shop(tenant, owner, api.StoreClient(tenant, NextIp()));
    }

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static object Body(string token, (Guid Id, int Quantity)[] items, decimal expectedTotal, string? serviceId = "fake-economico",
        bool pickup = false, BuyerDetails? buyer = null) => new
    {
        token,
        items = items.Select(i => new { partId = i.Id, quantity = i.Quantity }),
        expectedTotal,
        buyer = buyer ?? TestBuyers.Buyer(),
        delivery = new { method = pickup ? "pickup" : "shipping", address = pickup ? null : TestBuyers.Address(), serviceId = pickup ? null : serviceId },
    };

    private static async Task<decimal> FreightAsync(HttpClient store, params (Guid Id, int Quantity)[] items)
    {
        var response = await store.PostAsJsonAsync("/api/loja/frete", new { cep = "88015-200", items = items.Select(i => new { partId = i.Id, quantity = i.Quantity }) });
        return (await response.Content.ReadFromJsonAsync<QuoteDto>())!.Options.Single(o => o.ServiceId == "fake-economico").Price;
    }

    private static Task<HttpResponseMessage> PlaceAsync(HttpClient store, object body) => store.PostAsJsonAsync("/api/loja/pedidos", body);

    private async Task<StockDto> StockAsync(Shop shop, Guid part) =>
        (await (await GetAsync(api.PanelClient(), shop.Owner, $"/api/painel/pecas/{part}")).Content.ReadFromJsonAsync<PartDto>())!.Stock;

    private async Task<PageDto> PanelOrdersAsync(Shop shop) =>
        (await (await GetAsync(api.PanelClient(), shop.Owner, "/api/painel/pedidos")).Content.ReadFromJsonAsync<PageDto>())!;

    private async Task<T> InTenantAsync<T>(Guid tenantId, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        return await action(scope.ServiceProvider);
    }

    private Task<int> TenantSqlAsync(Guid tenantId, FormattableString sql) =>
        InTenantAsync(tenantId, sp => sp.GetRequiredService<TenantDbContext>().Database.ExecuteSqlAsync(sql));

    private async Task<T> InvokeAsync<T>(Guid tenantId, object command)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeForTenantAsync<T>(tenantId.ToString(), command);
    }

    private async Task InvokeAsync(Guid tenantId, object command)
    {
        await using var scope = api.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeForTenantAsync(tenantId.ToString(), command);
    }

    [Fact]
    public async Task Pedido_com_entrega_reserva_as_pecas_numera_e_aparece_no_painel()
    {
        var shop = await ShopAsync();
        var a = await api.CreatePartAsync(shop.Owner, price: 120m, quantity: 3, title: "Farol esquerdo");
        var b = await api.CreatePartAsync(shop.Owner, price: 35.5m, quantity: 1, title: "Lanterna");
        var freight = await FreightAsync(shop.Store, (a, 2), (b, 1));
        var token = NewToken();
        var buyer = TestBuyers.Buyer();

        var response = await PlaceAsync(shop.Store, Body(token, [(a, 2), (b, 1)], 2 * 120m + 35.5m + freight, buyer: buyer));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var placed = (await response.Content.ReadFromJsonAsync<PlacedDto>())!;
        Assert.Equal((1L, 275.5m + freight), (placed.Number, placed.Total));
        Assert.Equal((3, 2, 1), (await StockAsync(shop, a)) is var sa ? (sa.OnHand, sa.Reserved, sa.Available) : default);
        Assert.Equal((1, 1, 0), (await StockAsync(shop, b)) is var sb ? (sb.OnHand, sb.Reserved, sb.Available) : default);

        // Comprador acompanha pelo link: sem CPF, e-mail ou telefone na resposta.
        var lookup = await shop.Store.PostAsJsonAsync("/api/loja/pedidos/consulta", new { token });
        var json = await lookup.Content.ReadAsStringAsync();
        Assert.DoesNotContain(buyer.Cpf, json, StringComparison.Ordinal);
        Assert.DoesNotContain("maria@", json, StringComparison.Ordinal);
        var view = (await lookup.Content.ReadFromJsonAsync<StoreOrderDto>())!;
        Assert.Equal((1L, "pendingPayment", "shipping", "Econômico"), (view.Number, view.Status, view.DeliveryMethod, view.Service));
        Assert.Equal(new AddressDto("88015200", "Rua Felipe Schmidt", "100", "sala 2", "Centro", "Florianópolis", "SC"), view.Address);
        Assert.Equal((275.5m, freight, placed.Total), (view.ItemsTotal, view.ShippingTotal, view.Total));
        Assert.Equal(["Farol esquerdo", "Lanterna"], view.Items.OrderBy(i => i.Title).Select(i => i.Title));

        // Lojista vê o pedido completo; o próximo pedido (retirada) recebe o número seguinte.
        var page = await PanelOrdersAsync(shop);
        var summary = Assert.Single(page.Items);
        Assert.Equal((1L, "pendingPayment", "Maria da Silva", 3, placed.Total), (summary.Number, summary.Status, summary.BuyerName, summary.ItemCount, summary.Total));
        var detail = (await (await GetAsync(api.PanelClient(), shop.Owner, $"/api/painel/pedidos/{summary.Id}")).Content.ReadFromJsonAsync<OrderDto>())!;
        Assert.Equal((buyer.Cpf, "48999991234"), (detail.Buyer.Cpf, detail.Buyer.Phone));

        var pickup = await PlaceAsync(shop.Store, Body(NewToken(), [(a, 1)], 120m, pickup: true));
        Assert.Equal(HttpStatusCode.Created, pickup.StatusCode);
        Assert.Equal((2L, 120m), (await pickup.Content.ReadFromJsonAsync<PlacedDto>()) is { } p ? (p.Number, p.Total) : default);
        Assert.Equal(0, (await StockAsync(shop, a)).Available);
    }

    /// <summary>
    /// Mesmo token enviado de novo (duplo clique, queda de rede): com a última unidade, a peça já está reservada pelo próprio
    /// pedido; com saldo sobrando, o índice único do token recusa o segundo. Nos dois casos volta o pedido já criado.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Envio_repetido_devolve_o_mesmo_pedido_sem_reservar_de_novo(int onHand)
    {
        var shop = await ShopAsync();
        var part = await api.CreatePartAsync(shop.Owner, price: 50m, quantity: onHand);
        var body = Body(NewToken(), [(part, 1)], 50m, pickup: true);

        var responses = await Task.WhenAll(PlaceAsync(shop.Store, body), PlaceAsync(shop.Store, body));
        var again = await PlaceAsync(shop.Store, body);

        Assert.All(responses.Append(again), r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var numbers = await Task.WhenAll(responses.Append(again).Select(async r => (await r.Content.ReadFromJsonAsync<PlacedDto>())!.Number));
        Assert.Equal([1L, 1L, 1L], numbers);
        Assert.Equal(1, (await StockAsync(shop, part)).Reserved);
        Assert.Equal(1, (await PanelOrdersAsync(shop)).Total);
    }

    [Fact]
    public async Task Mudancas_desde_o_carrinho_nao_criam_pedido()
    {
        var shop = await ShopAsync();
        var part = await api.CreatePartAsync(shop.Owner, price: 80m, quantity: 1);
        var freight = await FreightAsync(shop.Store, (part, 1));

        var wrongTotal = await PlaceAsync(shop.Store, Body(NewToken(), [(part, 1)], 80m));
        var gone = await PlaceAsync(shop.Store, Body(NewToken(), [(part, 1)], 80m + freight, serviceId: "nao-existe"));
        var tooMany = await PlaceAsync(shop.Store, Body(NewToken(), [(part, 2)], 160m + freight));

        Assert.Equal((HttpStatusCode.Conflict, "O total do pedido mudou. Confira os valores antes de confirmar."), (wrongTotal.StatusCode, await TitleAsync(wrongTotal)));
        Assert.Equal((HttpStatusCode.Conflict, "A opção de frete escolhida não está mais disponível. Calcule o frete de novo."), (gone.StatusCode, await TitleAsync(gone)));
        Assert.Equal((HttpStatusCode.Conflict, "A disponibilidade de alguma peça mudou. Revise o carrinho antes de confirmar."), (tooMany.StatusCode, await TitleAsync(tooMany)));
        Assert.Equal(0, (await StockAsync(shop, part)).Reserved);
        Assert.Equal(0, (await PanelOrdersAsync(shop)).Total);
    }

    [Fact]
    public async Task Dados_invalidos_sao_recusados()
    {
        var shop = await ShopAsync(pickup: false);
        var part = await api.CreatePartAsync(shop.Owner, price: 80m);
        var buyer = TestBuyers.Buyer() with { Cpf = "123.456.789-00" };

        var badCpf = await PlaceAsync(shop.Store, Body(NewToken(), [(part, 1)], 80m, buyer: buyer));
        var badToken = await PlaceAsync(shop.Store, Body("curto", [(part, 1)], 80m));
        var noPickup = await PlaceAsync(shop.Store, Body(NewToken(), [(part, 1)], 80m, pickup: true));
        var empty = await PlaceAsync(shop.Store, Body(NewToken(), [], 0m, pickup: true));

        Assert.Equal((HttpStatusCode.BadRequest, "CPF inválido."), (badCpf.StatusCode, await TitleAsync(badCpf)));
        Assert.Equal(HttpStatusCode.BadRequest, badToken.StatusCode);
        Assert.Equal((HttpStatusCode.BadRequest, "Esta loja não oferece retirada."), (noPickup.StatusCode, await TitleAsync(noPickup)));
        Assert.Equal((HttpStatusCode.BadRequest, "O carrinho está vazio."), (empty.StatusCode, await TitleAsync(empty)));
        Assert.Equal(0, (await StockAsync(shop, part)).Reserved);
    }

    [Fact]
    public async Task Ultima_unidade_disputada_por_varios_compradores_vende_uma_so_vez()
    {
        var shop = await ShopAsync();
        var part = await api.CreatePartAsync(shop.Owner, price: 300m, quantity: 1);

        var attempts = Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
            PlaceAsync(api.StoreClient(shop.Tenant, NextIp()), Body(NewToken(), [(part, 1)], 300m, pickup: true))));
        var responses = await Task.WhenAll(attempts);

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        var stock = await StockAsync(shop, part);
        Assert.Equal((1, 1, 0), (stock.OnHand, stock.Reserved, stock.Available));
        Assert.Equal(1, (await PanelOrdersAsync(shop)).Total);
    }

    [Fact]
    public async Task Pedido_de_uma_loja_nao_aparece_em_outra()
    {
        var (a, b) = (await ShopAsync(), await ShopAsync());
        var partA = await api.CreatePartAsync(a.Owner, price: 10m);
        var token = NewToken();
        Assert.Equal(HttpStatusCode.Created, (await PlaceAsync(a.Store, Body(token, [(partA, 1)], 10m, pickup: true))).StatusCode);
        var orderA = (await PanelOrdersAsync(a)).Items.Single();

        Assert.Equal(HttpStatusCode.NotFound, (await b.Store.PostAsJsonAsync("/api/loja/pedidos/consulta", new { token })).StatusCode);
        Assert.Equal(0, (await PanelOrdersAsync(b)).Total);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(api.PanelClient(), b.Owner, $"/api/painel/pedidos/{orderA.Id}")).StatusCode);
        // Peça da loja A comprada pela vitrine da loja B: não existe lá.
        Assert.Equal(HttpStatusCode.Conflict, (await PlaceAsync(b.Store, Body(NewToken(), [(partA, 1)], 10m, pickup: true))).StatusCode);

        var partB = await api.CreatePartAsync(b.Owner, price: 10m);
        var placedB = await PlaceAsync(b.Store, Body(NewToken(), [(partB, 1)], 10m, pickup: true));
        Assert.Equal(1L, (await placedB.Content.ReadFromJsonAsync<PlacedDto>())!.Number);
        Assert.Equal(0, await InTenantAsync(b.Tenant.Id, sp => sp.GetRequiredService<TenantDbContext>().CustomerOrders.CountAsync(o => o.Id == orderA.Id)));
    }

    [Fact]
    public async Task Reserva_de_varias_pecas_e_tudo_ou_nada()
    {
        var shop = await ShopAsync();
        // As peças são reservadas em ordem de id: a primeira é reservada e precisa ser desfeita quando a segunda falha.
        var ids = new[] { await api.CreatePartAsync(shop.Owner, price: 10m, quantity: 1), await api.CreatePartAsync(shop.Owner, price: 10m, quantity: 1) }.Order().ToArray();
        var (first, last) = (ids[0], ids[1]);
        Assert.Equal(HttpStatusCode.Created, (await PlaceAsync(shop.Store, Body(NewToken(), [(last, 1)], 10m, pickup: true))).StatusCode);
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(NewToken()));
        PlaceOrderLine Line(Guid id) => new(id, "K-1", "Peça", 10m, 1);

        var result = await InvokeAsync<PlaceOrderResult>(shop.Tenant.Id,
            new PlaceOrder(hash, TestBuyers.Buyer(), null, null, "Balcão", [Line(first), Line(last)]));

        Assert.Equal((PlaceOrderOutcome.OutOfStock, (Guid?)last), (result.Outcome, result.UnavailablePartId));
        Assert.Equal((1, 0, 1), (await StockAsync(shop, first)) is var s ? (s.OnHand, s.Reserved, s.Available) : default);
        Assert.Equal(1, (await PanelOrdersAsync(shop)).Total);
        Assert.Equal(1, await InTenantAsync(shop.Tenant.Id, sp => sp.GetRequiredService<TenantDbContext>().StockReservations.CountAsync()));

        // O número não foi consumido pela tentativa desfeita.
        var placed = await InvokeAsync<PlaceOrderResult>(shop.Tenant.Id, new PlaceOrder(hash, TestBuyers.Buyer(), null, null, "Balcão", [Line(first)]));
        Assert.Equal((PlaceOrderOutcome.Placed, (long?)2), (placed.Outcome, placed.Number));
    }

    [Fact]
    public async Task Pedido_nao_pago_expira_no_prazo_e_devolve_as_pecas()
    {
        var shop = await ShopAsync();
        var part = await api.CreatePartAsync(shop.Owner, price: 40m, quantity: 2);
        var token = NewToken();
        Assert.Equal(HttpStatusCode.Created, (await PlaceAsync(shop.Store, Body(token, [(part, 2)], 80m, pickup: true))).StatusCode);
        var orderId = (await PanelOrdersAsync(shop)).Items.Single().Id;

        await using (var connection = new NpgsqlConnection(api.TenantsConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM wolverine.wolverine_incoming_envelopes WHERE status = 'Scheduled' AND message_type LIKE '%ExpireOrder%'", connection);
            Assert.True((long)(await command.ExecuteScalarAsync())! >= 1, "Expiração do pedido não foi agendada.");
        }

        await InvokeAsync(shop.Tenant.Id, new ExpireOrder(orderId)); // antes do prazo: nada muda
        Assert.Equal("pendingPayment", (await PanelOrdersAsync(shop)).Items.Single().Status);
        Assert.Equal(2, (await StockAsync(shop, part)).Reserved);

        await TenantSqlAsync(shop.Tenant.Id, $"UPDATE customer_orders SET payment_deadline = now() - interval '1 minute' WHERE id = {orderId}");
        await TenantSqlAsync(shop.Tenant.Id, $"UPDATE stock_reservations SET expires_at = now() - interval '1 minute'");
        await InvokeAsync(shop.Tenant.Id, new ExpireOrder(orderId));
        await InvokeAsync(shop.Tenant.Id, new ExpireOrder(orderId)); // repetir não devolve duas vezes

        var view = (await (await shop.Store.PostAsJsonAsync("/api/loja/pedidos/consulta", new { token })).Content.ReadFromJsonAsync<StoreOrderDto>())!;
        Assert.Equal(("canceled", "Pagamento não confirmado no prazo."), (view.Status, view.CancelReason));
        var stock = await StockAsync(shop, part);
        Assert.Equal((2, 0, 2), (stock.OnHand, stock.Reserved, stock.Available));
    }

    [Fact]
    public async Task Pedidos_tem_limite_por_ip()
    {
        var shop = await ShopAsync();
        var store = api.StoreClient(shop.Tenant, NextIp());

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++) statuses.Add((await PlaceAsync(store, Body("curto", [], 0m))).StatusCode);

        Assert.All(statuses.Take(10), s => Assert.Equal(HttpStatusCode.BadRequest, s));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[10]);
    }
}
