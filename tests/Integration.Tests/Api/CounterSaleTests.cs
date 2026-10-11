using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Ecommerce.Application.Inventory;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Inventory;
using Ecommerce.Domain.Orders;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Ecommerce.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF18 — venda de balcão: baixa atômica de todas as peças (ou nenhuma), pedido já pago e retirado na lista com origem
/// "counter", sem venda dupla entre balcões concorrentes e sem lançar duas vezes o mesmo formulário.
/// </summary>
public sealed class CounterSaleTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record BuyerDto(string Name, string Email, string Phone, string Cpf, string? Cnpj);
    private sealed record ItemDto(Guid PartId, decimal UnitPrice, int Quantity);
    private sealed record HistoryDto(string? From, string To, string Actor, string? UserEmail, string? Note);
    private sealed record OrderDto(Guid Id, long Number, string Origin, string Status, decimal Total, string? PaymentMethod, BuyerDto Buyer,
        DateTimeOffset? PaidAt, DateTimeOffset? DeliveredAt, List<ItemDto> Items, List<HistoryDto> History, string? CanceledBy);
    private sealed record SummaryDto(Guid Id, string Origin, string Status);
    private sealed record PageDto(List<SummaryDto> Items, int Total);
    private sealed record StockDto(int OnHand, int Reserved, int Available);
    private sealed record PartDto(Guid Id, StockDto Stock);

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private Task<HttpResponseMessage> SellAsync(string session, object body) =>
        SendAsync(api.PanelClient(), session, HttpMethod.Post, "/api/painel/balcao", body);

    private Task<HttpResponseMessage> SellAsync(string session, Guid part, int quantity, decimal price, string? token = null, object? customer = null) =>
        SellAsync(session, new { token = token ?? NewToken(), items = new[] { new { partId = part, quantity, unitPrice = price } }, paymentMethod = "pix", customer });

    private async Task<StockDto> StockAsync(string session, Guid part) =>
        (await (await GetAsync(api.PanelClient(), session, $"/api/painel/pecas/{part}")).Content.ReadFromJsonAsync<PartDto>())!.Stock;

    private async Task<T> InTenantAsync<T>(Guid tenantId, Func<TenantDbContext, Task<T>> query)
    {
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        return await query(scope.ServiceProvider.GetRequiredService<TenantDbContext>());
    }

    [Fact]
    public async Task Venda_baixa_o_estoque_e_vira_pedido_pago_e_retirado_com_preco_praticado()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var farol = await api.CreatePartAsync(owner, price: 120m, quantity: 3);
        var lanterna = await api.CreatePartAsync(owner, price: 80m, quantity: 1, active: false);

        var response = await SellAsync(owner, new
        {
            token = NewToken(),
            items = new[] { new { partId = farol, quantity = 2, unitPrice = 100m }, new { partId = lanterna, quantity = 1, unitPrice = 80m } },
            paymentMethod = "cash",
            customer = new { name = "Oficina do Zé", document = "11.222.333/0001-81", phone = "(48) 3222-1234" },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var sale = (await response.Content.ReadFromJsonAsync<OrderDto>())!;
        Assert.Equal(("counter", "pickedUp", "cash", 280m), (sale.Origin, sale.Status, sale.PaymentMethod, sale.Total));
        Assert.Equal(("Oficina do Zé", "11222333000181", "", "4832221234"), (sale.Buyer.Name, sale.Buyer.Cnpj, sale.Buyer.Cpf, sale.Buyer.Phone));
        Assert.NotNull(sale.PaidAt);
        Assert.NotNull(sale.DeliveredAt);
        Assert.Equal(100m, sale.Items.Single(i => i.PartId == farol).UnitPrice);
        var created = Assert.Single(sale.History);
        Assert.Equal((null, "pickedUp", "user"), (created.From, created.To, created.Actor));
        Assert.EndsWith("@pecas.test", created.UserEmail, StringComparison.Ordinal);
        Assert.Contains("pago em dinheiro", created.Note, StringComparison.Ordinal);
        Assert.Contains("de R$ 120,00 por R$ 100,00", created.Note, StringComparison.Ordinal);

        Assert.Equal((1, 0, 1), ((await StockAsync(owner, farol)).OnHand, (await StockAsync(owner, farol)).Reserved, (await StockAsync(owner, farol)).Available));
        Assert.Equal(0, (await StockAsync(owner, lanterna)).OnHand);
        var movements = await InTenantAsync(tenant.Id, db => db.StockMovements.Where(m => m.Reason == StockMovementReason.DirectSale).ToListAsync());
        Assert.Equal(2, movements.Count);
        Assert.All(movements, m => Assert.NotNull(m.UserId));
        Assert.Equal(-2, movements.Single(m => m.PartId == farol).Delta);

        // Na lista de pedidos, com origem balcão e busca por CNPJ.
        var page = (await (await GetAsync(api.PanelClient(), owner, "/api/painel/pedidos?origem=counter&busca=11.222.333")).Content.ReadFromJsonAsync<PageDto>())!;
        Assert.Equal(sale.Id, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task Sem_saldo_de_uma_peca_nada_e_baixado()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var farol = await api.CreatePartAsync(owner, price: 120m, quantity: 2);
        var retrovisor = await api.CreatePartAsync(owner, price: 60m, quantity: 1, title: "Retrovisor esquerdo");

        var response = await SellAsync(owner, new
        {
            token = NewToken(),
            items = new[] { new { partId = farol, quantity = 1, unitPrice = 120m }, new { partId = retrovisor, quantity = 2, unitPrice = 60m } },
            paymentMethod = "pix",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Retrovisor esquerdo", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(2, (await StockAsync(owner, farol)).OnHand);
        Assert.Equal(1, (await StockAsync(owner, retrovisor)).OnHand);
        Assert.Equal(0, await InTenantAsync(tenant.Id, db => db.CustomerOrders.CountAsync()));
    }

    [Fact]
    public async Task Unidade_reservada_por_pedido_do_site_nao_e_vendida_no_balcao()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var part = await api.CreatePartAsync(owner, price: 90m, quantity: 2);
        await using (var scope = api.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IMessageBus>()
                .InvokeForTenantAsync<ReserveStockResult>(tenant.Id.ToString(), new ReserveStock(part, 1, "pedido do site"));

        Assert.Equal(HttpStatusCode.Conflict, (await SellAsync(owner, part, 2, 90m)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SellAsync(owner, part, 1, 90m)).StatusCode);
        Assert.Equal((1, 1, 0), ((await StockAsync(owner, part)).OnHand, (await StockAsync(owner, part)).Reserved, (await StockAsync(owner, part)).Available));
    }

    [Fact]
    public async Task Balcoes_concorrentes_nao_vendem_a_mesma_ultima_unidade()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var operatorSession = await api.SessionForAsync(tenant, TenantRole.Operator);
        var part = await api.CreatePartAsync(owner, price: 90m, quantity: 1);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => SellAsync(i % 2 == 0 ? owner : operatorSession, part, 1, 90m)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal((0, 0), ((await StockAsync(owner, part)).OnHand, (await StockAsync(owner, part)).Reserved));
        Assert.Equal(1, await InTenantAsync(tenant.Id, db => db.CustomerOrders.CountAsync()));
    }

    [Fact]
    public async Task Mesmo_formulario_enviado_duas_vezes_lanca_uma_venda_so()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var part = await api.CreatePartAsync(owner, price: 50m, quantity: 5);
        var token = NewToken();

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => SellAsync(owner, part, 1, 50m, token)));

        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, r.StatusCode.ToString()));
        var ids = await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<OrderDto>())!.Id));
        Assert.Single(ids.Distinct());
        Assert.Equal(4, (await StockAsync(owner, part)).OnHand);
    }

    [Fact]
    public async Task Reenvio_depois_de_a_venda_zerar_o_estoque_devolve_a_mesma_venda()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var part = await api.CreatePartAsync(owner, price: 50m, quantity: 1);
        var token = NewToken();

        var first = await SellAsync(owner, part, 1, 50m, token);
        var again = await SellAsync(owner, part, 1, 50m, token);

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.OK), (first.StatusCode, again.StatusCode));
        Assert.Equal((await first.Content.ReadFromJsonAsync<OrderDto>())!.Id, (await again.Content.ReadFromJsonAsync<OrderDto>())!.Id);
        Assert.Equal(0, (await StockAsync(owner, part)).OnHand);
    }

    [Fact]
    public async Task Cancelar_venda_de_balcao_devolve_as_pecas()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var part = await api.CreatePartAsync(owner, price: 70m, quantity: 2);
        var sale = (await (await SellAsync(owner, part, 2, 70m)).Content.ReadFromJsonAsync<OrderDto>())!;
        Assert.Equal(0, (await StockAsync(owner, part)).OnHand);

        var canceled = await SendAsync(api.PanelClient(), owner, HttpMethod.Post, $"/api/painel/pedidos/{sale.Id}/cancelar", new { motivo = "Lançada em duplicidade" });

        Assert.Equal(HttpStatusCode.OK, canceled.StatusCode);
        var view = (await canceled.Content.ReadFromJsonAsync<OrderDto>())!;
        Assert.Equal(("canceled", "store"), (view.Status, view.CanceledBy));
        Assert.Equal(2, (await StockAsync(owner, part)).OnHand);
        Assert.Equal(HttpStatusCode.Conflict,
            (await SendAsync(api.PanelClient(), owner, HttpMethod.Post, $"/api/painel/pedidos/{sale.Id}/cancelar", new { motivo = "De novo" })).StatusCode);
        Assert.Equal(2, (await StockAsync(owner, part)).OnHand);
    }

    [Fact]
    public async Task Dados_invalidos_sao_recusados_sem_mexer_no_estoque()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var part = await api.CreatePartAsync(owner, price: 40m, quantity: 1);
        var item = new[] { new { partId = part, quantity = 1, unitPrice = 40m } };

        Assert.Equal(HttpStatusCode.BadRequest, (await SellAsync(owner, new { token = "curto", items = item, paymentMethod = "pix" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SellAsync(owner, new { token = NewToken(), items = item })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SellAsync(owner, part, 1, 40m, customer: new { document = "123.456.789-00" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SellAsync(owner, part, 1, 0.001m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SellAsync(owner, Guid.CreateVersion7(), 1, 40m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SellAsync(owner, new { token = NewToken(), items = Array.Empty<object>(), paymentMethod = "pix" })).StatusCode);
        Assert.Equal(1, (await StockAsync(owner, part)).OnHand);
    }

    [Fact]
    public async Task Peca_de_outra_loja_nao_e_vendida()
    {
        var mine = await api.CreateTenantAsync();
        var other = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(mine);
        var foreignPart = await api.CreatePartAsync(await api.SessionForAsync(other), price: 40m, quantity: 1);

        Assert.Equal(HttpStatusCode.BadRequest, (await SellAsync(owner, foreignPart, 1, 40m)).StatusCode);
        Assert.Equal(1, await InTenantAsync(other.Id, db => db.Stocks.Where(s => s.PartId == foreignPart).Select(s => s.OnHand).SingleAsync()));
    }
}
