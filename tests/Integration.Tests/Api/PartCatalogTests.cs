using System.Net;
using System.Net.Http.Json;
using Ecommerce.Application.Inventory;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Inventory;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Ecommerce.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>RF08 — cadastro de peça: dados, código único por loja, busca, quantidade atômica e isolamento.</summary>
public sealed class PartCatalogTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record StockDto(int OnHand, int Reserved, int Available);
    private sealed record PartDto(Guid Id, string InternalCode, string Title, string Description, string Condition, decimal Price,
        int? LengthCm, int? WidthCm, int? HeightCm, int? WeightG, List<string> OemCodes, string Status, StockDto Stock, bool HasShippingDimensions);
    private sealed record PageDto(List<PartDto> Items, int Total, int Page, int PageSize);

    private static object Body(string? code = "FAR-001", string title = "Farol dianteiro esquerdo Gol G5", decimal price = 350m,
        int quantity = 2, string[]? oem = null, int? weight = 2500) => new
    {
        internalCode = code, title, description = "Original, sem trincas.", condition = "used", price,
        lengthCm = 60, widthCm = 40, heightCm = 30, weightG = weight, oemCodes = oem ?? ["5U0 941 015"], quantity,
    };

    private static string Code() => "P-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();

    private async Task<PartDto> CreateAsync(string token, object body)
    {
        var response = await SendAsync(api.PanelClient(), token, HttpMethod.Post, "/api/painel/pecas", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PartDto>())!;
    }

    private async Task AddPhotoAsync(string token, Guid partId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/painel/pecas/{partId}/fotos") { Content = new ByteArrayContent(TestImages.Jpeg(200, 150)) };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Created, (await api.PanelClient().SendAsync(request)).StatusCode);
    }

    private async Task<T> InTenantAsync<T>(Guid tenantId, Func<TenantDbContext, Task<T>> query)
    {
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        return await query(scope.ServiceProvider.GetRequiredService<TenantDbContext>());
    }

    [Fact]
    public async Task Cadastro_grava_os_dados_o_saldo_e_o_movimento_com_o_usuario()
    {
        var tenant = await api.CreateTenantAsync();
        var user = await api.CreateUserAsync(tenant.Id, TenantRole.Operator, Password); // operador cadastra peças
        var token = (await LoginAsync(api.PanelClient(), user.Email, Password)).Session!.AccessToken;

        var part = await CreateAsync(token, Body(code: " far-001 ", quantity: 3, oem: ["5U0 941 015", "5u0941015", "6R0.941.007"]));

        Assert.Equal("FAR-001", part.InternalCode);
        Assert.Equal("draft", part.Status);
        Assert.Equal("used", part.Condition);
        Assert.Equal(["5U0941015", "6R0941007"], part.OemCodes);
        Assert.Equal(new StockDto(3, 0, 3), part.Stock);
        Assert.True(part.HasShippingDimensions);
        var movement = await InTenantAsync(tenant.Id, db => db.StockMovements.SingleAsync(m => m.PartId == part.Id));
        Assert.Equal((3, StockMovementReason.Adjustment, (Guid?)user.Id), (movement.Delta, movement.Reason, movement.UserId));
    }

    [Fact]
    public async Task Codigo_interno_e_unico_na_loja_mas_pode_repetir_em_outra()
    {
        var (a, b) = (await api.CreateTenantAsync(), await api.CreateTenantAsync());
        var (ownerA, ownerB) = (await api.SessionForAsync(a), await api.SessionForAsync(b));
        await CreateAsync(ownerA, Body(code: "MOT-01"));

        var repeated = await SendAsync(api.PanelClient(), ownerA, HttpMethod.Post, "/api/painel/pecas", Body(code: "mot-01"));

        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
        Assert.Equal("Já existe uma peça com este código interno.", await TitleAsync(repeated));
        await CreateAsync(ownerB, Body(code: "MOT-01"));
    }

    [Fact]
    public async Task Cadastros_simultaneos_com_o_mesmo_codigo_criam_uma_peca()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var code = Code();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
            SendAsync(api.PanelClient(), owner, HttpMethod.Post, "/api/painel/pecas", Body(code: code)))));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(1, await InTenantAsync(tenant.Id, db => db.Parts.CountAsync(p => p.InternalCode == code)));
    }

    [Fact]
    public async Task Peca_de_outra_loja_nao_aparece_nem_e_alterada()
    {
        var (a, b) = (await api.CreateTenantAsync(), await api.CreateTenantAsync());
        var (ownerA, ownerB) = (await api.SessionForAsync(a), await api.SessionForAsync(b));
        var partA = await CreateAsync(ownerA, Body(code: Code()));
        var client = api.PanelClient();

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(client, ownerB, $"/api/painel/pecas/{partA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(client, ownerB, HttpMethod.Put, $"/api/painel/pecas/{partA.Id}", Body(code: null, title: "Invadido"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(client, ownerB, HttpMethod.Put, $"/api/painel/pecas/{partA.Id}/situacao", new { status = "inactive" })).StatusCode);
        var listB = await (await GetAsync(client, ownerB, "/api/painel/pecas")).Content.ReadFromJsonAsync<PageDto>();
        Assert.DoesNotContain(listB!.Items, p => p.Id == partA.Id);
        Assert.Equal("Farol dianteiro esquerdo Gol G5", (await (await GetAsync(client, ownerA, $"/api/painel/pecas/{partA.Id}")).Content.ReadFromJsonAsync<PartDto>())!.Title);
    }

    [Fact]
    public async Task Busca_por_titulo_codigo_e_oem_com_filtro_e_paginacao()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var farol = await CreateAsync(owner, Body(code: "FAR-100", title: "Farol Gol G5", oem: ["5U0 941 015"]));
        var lanterna = await CreateAsync(owner, Body(code: "LAN-200", title: "Lanterna traseira 100% original", oem: ["5U6945095"]));
        var motor = await CreateAsync(owner, Body(code: "MOT-300", title: "Motor de partida", oem: []));
        await AddPhotoAsync(owner, motor.Id);
        await SendAsync(api.PanelClient(), owner, HttpMethod.Put, $"/api/painel/pecas/{motor.Id}/situacao", new { status = "active" });

        async Task<List<Guid>> Search(string query) =>
            (await (await GetAsync(api.PanelClient(), owner, "/api/painel/pecas?" + query)).Content.ReadFromJsonAsync<PageDto>())!.Items.Select(p => p.Id).ToList();

        Assert.Equal([farol.Id], await Search("busca=gol"));
        Assert.Equal([lanterna.Id], await Search("busca=lan-2"));
        Assert.Equal([farol.Id], await Search("busca=5u0-941-015"));
        Assert.Equal([lanterna.Id], await Search("busca=100%25"));   // % é literal, não curinga
        Assert.Equal([motor.Id], await Search("situacao=active"));
        var page = await (await GetAsync(api.PanelClient(), owner, "/api/painel/pecas?tamanho=2&pagina=2")).Content.ReadFromJsonAsync<PageDto>();
        Assert.Equal((3, 1), (page!.Total, page.Items.Count));
    }

    [Fact]
    public async Task Edicao_ajusta_o_saldo_e_registra_o_movimento()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var part = await CreateAsync(owner, Body(code: Code(), quantity: 5));

        var edited = await SendAsync(api.PanelClient(), owner, HttpMethod.Put, $"/api/painel/pecas/{part.Id}",
            Body(code: null, title: "Farol Gol G5 (lado esquerdo)", quantity: 2, oem: ["AAA111"], weight: null));

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var view = (await edited.Content.ReadFromJsonAsync<PartDto>())!;
        Assert.Equal(("Farol Gol G5 (lado esquerdo)", new StockDto(2, 0, 2), false), (view.Title, view.Stock, view.HasShippingDimensions));
        Assert.Equal(["AAA111"], view.OemCodes);
        var deltas = await InTenantAsync(tenant.Id, db => db.StockMovements.Where(m => m.PartId == part.Id).OrderBy(m => m.At).Select(m => m.Delta).ToListAsync());
        Assert.Equal([5, -3], deltas);
    }

    [Fact]
    public async Task Quantidade_nao_fica_abaixo_do_reservado_e_nada_e_gravado()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var part = await CreateAsync(owner, Body(code: Code(), quantity: 3));
        var bus = api.Services.GetRequiredService<IMessageBus>();
        Assert.True((await bus.InvokeForTenantAsync<ReserveStockResult>(tenant.Id.ToString(), new ReserveStock(part.Id, 2, "pedido-1"))).Reserved);

        var refused = await SendAsync(api.PanelClient(), owner, HttpMethod.Put, $"/api/painel/pecas/{part.Id}", Body(code: null, title: "Título novo", quantity: 1));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("2 unidade(s) reservada(s)", await TitleAsync(refused), StringComparison.Ordinal);
        var view = await (await GetAsync(api.PanelClient(), owner, $"/api/painel/pecas/{part.Id}")).Content.ReadFromJsonAsync<PartDto>();
        Assert.Equal(("Farol dianteiro esquerdo Gol G5", new StockDto(3, 2, 1)), (view!.Title, view.Stock));
    }

    [Fact]
    public async Task Ajuste_e_reservas_simultaneos_nunca_deixam_reserva_maior_que_o_fisico()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var part = await CreateAsync(owner, Body(code: Code(), quantity: 10));
        var bus = api.Services.GetRequiredService<IMessageBus>();

        var reservations = Enumerable.Range(0, 10).Select(i => Task.Run(() =>
            bus.InvokeForTenantAsync<ReserveStockResult>(tenant.Id.ToString(), new ReserveStock(part.Id, 1, $"pedido-{i}"))));
        var adjustments = Enumerable.Range(0, 5).Select(_ => Task.Run(() =>
            SendAsync(api.PanelClient(), owner, HttpMethod.Put, $"/api/painel/pecas/{part.Id}", Body(code: null, quantity: 4))));
        var reserved = (await Task.WhenAll(reservations)).Count(r => r.Reserved);
        var adjusted = await Task.WhenAll(adjustments);

        Assert.All(adjusted, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        var stock = await InTenantAsync(tenant.Id, db => db.Stocks.SingleAsync(s => s.PartId == part.Id));
        Assert.Equal(reserved, stock.Reserved);
        Assert.True(stock.Reserved <= stock.OnHand);
        Assert.Equal(stock.OnHand, 10 + await InTenantAsync(tenant.Id, db => db.StockMovements.Where(m => m.PartId == part.Id && m.Reason == StockMovementReason.Adjustment && m.Delta != 10).SumAsync(m => m.Delta)));
    }

    [Theory]
    [InlineData(0, 1, "Preço precisa ser maior que zero, com até duas casas decimais.")]
    [InlineData(10, -1, "Quantidade precisa estar entre 0 e 1000000.")]
    public async Task Dados_invalidos_sao_recusados_com_mensagem(decimal price, int quantity, string message)
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);

        var refused = await SendAsync(api.PanelClient(), owner, HttpMethod.Post, "/api/painel/pecas", Body(code: Code(), price: price, quantity: quantity));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(message, await TitleAsync(refused));
        Assert.Equal(0, await InTenantAsync(tenant.Id, db => db.Parts.CountAsync()));
    }

    [Fact]
    public async Task Situacao_ativa_e_inativa_mas_nao_volta_a_rascunho()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var part = await CreateAsync(owner, Body(code: Code()));
        await AddPhotoAsync(owner, part.Id);

        async Task<HttpResponseMessage> Set(string status) =>
            await SendAsync(api.PanelClient(), owner, HttpMethod.Put, $"/api/painel/pecas/{part.Id}/situacao", new { status });

        Assert.Equal("active", (await (await Set("active")).Content.ReadFromJsonAsync<PartDto>())!.Status);
        Assert.Equal("inactive", (await (await Set("inactive")).Content.ReadFromJsonAsync<PartDto>())!.Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Set("draft")).StatusCode);
    }
}
