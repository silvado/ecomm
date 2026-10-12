using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Inventory;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF20 — devolução: parcial ou total, peça avariada não volta ao estoque, nunca acima do vendido (nem com devoluções
/// simultâneas), "Devolvido" quando tudo volta, venda com devolução não é mais cancelada e isolamento entre lojas.
/// </summary>
public sealed class OrderReturnApiTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record ItemDto(Guid PartId, int Quantity, int ReturnedQuantity);
    private sealed record LineDto(Guid PartId, string InternalCode, int Quantity, bool Restock);
    private sealed record ReturnDto(string Reason, string? Note, decimal? RefundAmount, string? RefundMethod, string? UserEmail, List<LineDto> Lines);
    private sealed record HistoryDto(string? From, string To, string? Note);
    private sealed record OrderDto(Guid Id, long Number, string Status, DateTimeOffset? ReturnedAt, List<ItemDto> Items, List<ReturnDto> Returns, List<HistoryDto> History);
    private sealed record StockDto(int OnHand, int Reserved, int Available);
    private sealed record PartDto(Guid Id, StockDto Stock);

    private static string Token() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Venda de balcão (já retirada) com as peças e quantidades pedidas.</summary>
    private async Task<OrderDto> SaleAsync(string session, params (Guid Part, int Quantity, decimal Price)[] items)
    {
        var response = await SendAsync(api.PanelClient(), session, HttpMethod.Post, "/api/painel/balcao", new
        {
            token = Token(), paymentMethod = "pix", items = items.Select(i => new { partId = i.Part, quantity = i.Quantity, unitPrice = i.Price }).ToArray(),
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OrderDto>())!;
    }

    private Task<HttpResponseMessage> ReturnAsync(string session, Guid order, object[] items, string reason = "defect", decimal? refund = null, string? method = null, string? note = null) =>
        SendAsync(api.PanelClient(), session, HttpMethod.Post, $"/api/painel/pedidos/{order}/devolucoes",
            new { items, reason, note, refundAmount = refund, refundMethod = method });

    private async Task<StockDto> StockAsync(string session, Guid part) =>
        (await (await GetAsync(api.PanelClient(), session, $"/api/painel/pecas/{part}")).Content.ReadFromJsonAsync<PartDto>())!.Stock;

    private async Task<T> InTenantAsync<T>(Guid tenantId, Func<TenantDbContext, Task<T>> query)
    {
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        return await query(scope.ServiceProvider.GetRequiredService<TenantDbContext>());
    }

    [Fact]
    public async Task Devolucao_parcial_volta_ao_estoque_so_o_que_nao_esta_avariado()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var farol = await api.CreatePartAsync(owner, price: 100m, quantity: 3);
        var lanterna = await api.CreatePartAsync(owner, price: 50m, quantity: 1);
        var sale = await SaleAsync(owner, (farol, 2, 100m), (lanterna, 1, 50m));
        Assert.Equal((1, 0), ((await StockAsync(owner, farol)).OnHand, (await StockAsync(owner, lanterna)).OnHand));

        var response = await ReturnAsync(owner, sale.Id,
            [new { partId = farol, quantity = 1, restock = true }, new { partId = lanterna, quantity = 1, restock = false }],
            reason: "defect", refund: 150m, method: "pix", note: "Lanterna chegou trincada");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = (await response.Content.ReadFromJsonAsync<OrderDto>())!;
        Assert.Equal("pickedUp", view.Status);
        Assert.Equal((1, 1), (view.Items.Single(i => i.PartId == farol).ReturnedQuantity, view.Items.Single(i => i.PartId == lanterna).ReturnedQuantity));
        var registered = Assert.Single(view.Returns);
        Assert.Equal(("defect", 150m, "pix"), (registered.Reason, registered.RefundAmount, registered.RefundMethod));
        Assert.EndsWith("@pecas.test", registered.UserEmail, StringComparison.Ordinal);
        Assert.Contains("1× ", view.History[^1].Note!, StringComparison.Ordinal);
        Assert.Contains("avariada", view.History[^1].Note!, StringComparison.Ordinal);
        Assert.Contains("reembolso R$ 150,00 em Pix", view.History[^1].Note!, StringComparison.Ordinal);

        Assert.Equal(2, (await StockAsync(owner, farol)).OnHand);
        Assert.Equal(0, (await StockAsync(owner, lanterna)).OnHand);
        var movement = await InTenantAsync(tenant.Id, db => db.StockMovements.SingleAsync(m => m.Reason == StockMovementReason.OrderReturned));
        Assert.Equal((farol, 1), (movement.PartId, movement.Delta));
        Assert.NotNull(movement.UserId);
    }

    [Fact]
    public async Task Tudo_devolvido_fica_devolvido_aparece_no_filtro_e_bloqueia_cancelamento_e_nova_devolucao()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var farol = await api.CreatePartAsync(owner, price: 100m, quantity: 2);
        var sale = await SaleAsync(owner, (farol, 2, 100m));

        Assert.Equal(HttpStatusCode.OK, (await ReturnAsync(owner, sale.Id, [new { partId = farol, quantity = 1, restock = true }])).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await SendAsync(api.PanelClient(), owner, HttpMethod.Post, $"/api/painel/pedidos/{sale.Id}/cancelar", new { motivo = "lançada errado" })).StatusCode);
        var done = (await (await ReturnAsync(owner, sale.Id, [new { partId = farol, quantity = 1, restock = true }])).Content.ReadFromJsonAsync<OrderDto>())!;

        Assert.Equal("returned", done.Status);
        Assert.NotNull(done.ReturnedAt);
        Assert.Equal(("pickedUp", "returned"), (done.History[^1].From, done.History[^1].To));
        Assert.Equal(2, (await StockAsync(owner, farol)).OnHand);
        Assert.Equal(HttpStatusCode.Conflict, (await ReturnAsync(owner, sale.Id, [new { partId = farol, quantity = 1, restock = true }])).StatusCode);
        var page = await (await GetAsync(api.PanelClient(), owner, "/api/painel/pedidos?situacao=returned")).Content.ReadFromJsonAsync<PageDto>();
        Assert.Equal(sale.Id, Assert.Single(page!.Items).Id);
    }

    private sealed record SummaryDto(Guid Id, string Status);
    private sealed record PageDto(List<SummaryDto> Items, int Total);

    [Fact]
    public async Task Devolucoes_simultaneas_nunca_passam_do_vendido()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var operatorSession = await api.SessionForAsync(tenant, TenantRole.Operator);
        var farol = await api.CreatePartAsync(owner, price: 100m, quantity: 1);
        var sale = await SaleAsync(owner, (farol, 1, 100m));

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            ReturnAsync(i % 2 == 0 ? owner : operatorSession, sale.Id, [new { partId = farol, quantity = 1, restock = true }])));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK),
            r => Assert.True(r.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest, r.StatusCode.ToString()));
        Assert.Equal(1, (await StockAsync(owner, farol)).OnHand);
        Assert.Equal(1, await InTenantAsync(tenant.Id, db => db.OrderReturns.CountAsync()));
    }

    [Fact]
    public async Task Dados_invalidos_e_pedido_nao_entregue_sao_recusados_sem_mexer_no_estoque()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var farol = await api.CreatePartAsync(owner, price: 100m, quantity: 2);
        var sale = await SaleAsync(owner, (farol, 1, 100m));

        Assert.Equal(HttpStatusCode.BadRequest, (await ReturnAsync(owner, sale.Id, [new { partId = farol, quantity = 2, restock = true }])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ReturnAsync(owner, sale.Id, [])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ReturnAsync(owner, sale.Id, [new { partId = farol, quantity = 1, restock = true }], refund: 100.01m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendAsync(api.PanelClient(), owner, HttpMethod.Post, $"/api/painel/pedidos/{sale.Id}/devolucoes", new { items = new[] { new { partId = farol, quantity = 1, restock = true } } })).StatusCode);
        Assert.Equal(1, (await StockAsync(owner, farol)).OnHand);
        Assert.Equal(0, await InTenantAsync(tenant.Id, db => db.OrderReturns.CountAsync()));

        Assert.Equal(HttpStatusCode.NotFound, (await ReturnAsync(owner, Guid.CreateVersion7(), [new { partId = farol, quantity = 1, restock = true }])).StatusCode);
    }

    [Fact]
    public async Task Outra_loja_nao_registra_devolucao()
    {
        var mine = await api.CreateTenantAsync();
        var other = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(mine);
        var stranger = await api.SessionForAsync(other);
        var farol = await api.CreatePartAsync(owner, price: 100m, quantity: 1);
        var sale = await SaleAsync(owner, (farol, 1, 100m));

        Assert.Equal(HttpStatusCode.NotFound, (await ReturnAsync(stranger, sale.Id, [new { partId = farol, quantity = 1, restock = true }])).StatusCode);
        Assert.Equal(0, (await StockAsync(owner, farol)).OnHand);
    }
}
