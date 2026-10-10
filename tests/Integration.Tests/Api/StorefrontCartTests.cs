using System.Net;
using System.Net.Http.Json;
using Ecommerce.Application.Shipping;
using Ecommerce.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>RF14 — carrinho e frete na loja pública: tudo recalculado no servidor, no tenant do Host.</summary>
public sealed class StorefrontCartTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record LineDto(Guid PartId, string Title, decimal UnitPrice, int Quantity, int Available, string? Problem);
    private sealed record CartDto(List<LineDto> Lines, decimal Subtotal);
    private sealed record OptionDto(string ServiceId, string Carrier, string Service, decimal Price, int DeliveryDays);
    private sealed record QuoteDto(List<OptionDto> Options, string? Pickup, string? Message);
    private static CartItem[] Items(params (Guid Id, int Quantity)[] items) => items.Select(i => new CartItem(i.Id, i.Quantity)).ToArray();

    [Fact]
    public async Task Carrinho_usa_preco_e_estoque_do_servidor()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var (a, b, rascunho) = (await api.CreatePartAsync(owner, price: 120m, quantity: 2), await api.CreatePartAsync(owner, price: 35.5m), await api.CreatePartAsync(owner, active: false));

        var response = await api.StoreClient(tenant).PostAsJsonAsync("/api/loja/carrinho", new { items = Items((a, 5), (b, 1), (rascunho, 1), (Guid.NewGuid(), 1)) });
        var cart = (await response.Content.ReadFromJsonAsync<CartDto>())!;

        Assert.Equal((120m, 2, "quantityReduced"), (cart.Lines.Single(l => l.PartId == a).UnitPrice, cart.Lines.Single(l => l.PartId == a).Quantity, cart.Lines.Single(l => l.PartId == a).Problem));
        Assert.Null(cart.Lines.Single(l => l.PartId == b).Problem);
        Assert.Equal("unavailable", cart.Lines.Single(l => l.PartId == rascunho).Problem);
        Assert.Equal(2 * 120m + 35.5m, cart.Subtotal);
    }

    [Fact]
    public async Task Peca_de_outra_loja_nao_entra_no_carrinho()
    {
        var (a, b) = (await api.CreateTenantAsync(), await api.CreateTenantAsync());
        var partA = await api.CreatePartAsync(await api.SessionForAsync(a));

        var cart = await (await api.StoreClient(b).PostAsJsonAsync("/api/loja/carrinho", new { items = Items((partA, 1)) })).Content.ReadFromJsonAsync<CartDto>();

        Assert.Equal("unavailable", cart!.Lines.Single().Problem);
        Assert.Equal(0m, cart.Subtotal);
    }

    [Fact]
    public async Task Frete_cotado_com_as_medidas_do_servidor_e_retirada_quando_habilitada()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var part = await api.CreatePartAsync(owner);
        Assert.Equal(HttpStatusCode.OK, (await api.ConfigureShippingAsync(owner, tenant, "01310-100", pickup: true, "Rua A, 1")).StatusCode);

        var quote = await (await api.StoreClient(tenant).PostAsJsonAsync("/api/loja/frete", new { cep = "88015-200", items = Items((part, 1)) })).Content.ReadFromJsonAsync<QuoteDto>();

        Assert.Equal(["fake-economico", "fake-expresso"], quote!.Options.Select(o => o.ServiceId));
        Assert.All(quote.Options, o => Assert.True(o.Price > 0));
        Assert.Equal("Rua A, 1", quote.Pickup);
        Assert.Null(quote.Message);
    }

    [Fact]
    public async Task Sem_cep_de_origem_ou_com_peca_sem_medidas_so_retirada()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var ok = await api.CreatePartAsync(owner);
        var noDims = await api.CreatePartAsync(owner, dimensions: false);
        var store = api.StoreClient(tenant);

        await api.ConfigureShippingAsync(owner, tenant, null, pickup: true, "Balcão");
        var withoutOrigin = await (await store.PostAsJsonAsync("/api/loja/frete", new { cep = "88015200", items = Items((ok, 1)) })).Content.ReadFromJsonAsync<QuoteDto>();
        Assert.Equal((0, "Balcão", "Esta loja ainda não faz entregas."), (withoutOrigin!.Options.Count, withoutOrigin.Pickup, withoutOrigin.Message));

        await api.ConfigureShippingAsync(owner, tenant, "01310100", pickup: false, null);
        var withNoDims = await (await store.PostAsJsonAsync("/api/loja/frete", new { cep = "88015200", items = Items((ok, 1), (noDims, 1)) })).Content.ReadFromJsonAsync<QuoteDto>();
        Assert.Empty(withNoDims!.Options);
        Assert.Null(withNoDims.Pickup);
        Assert.StartsWith("Há peça sem medidas", withNoDims.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cep_invalido_e_configuracao_invalida_sao_recusados()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var part = await api.CreatePartAsync(owner);

        var badCep = await api.StoreClient(tenant).PostAsJsonAsync("/api/loja/frete", new { cep = "123", items = Items((part, 1)) });
        Assert.Equal((HttpStatusCode.BadRequest, "CEP inválido: informe os 8 dígitos."), (badCep.StatusCode, await TitleAsync(badCep)));

        var badPickup = await api.ConfigureShippingAsync(owner, tenant, "01310100", pickup: true, null);
        Assert.Equal((HttpStatusCode.BadRequest, "Informe o endereço de retirada."), (badPickup.StatusCode, await TitleAsync(badPickup)));
    }

    private sealed class FailingProvider : IShippingProvider
    {
        public Task<IReadOnlyList<ShippingOption>> QuoteAsync(ShippingQuoteRequest request, CancellationToken ct = default) =>
            throw new ShippingProviderException("A conexão com o Melhor Envio expirou ou foi revogada.");
    }

    private sealed class FailingResolver : IShippingProviderResolver
    {
        public Task<IShippingProvider?> ResolveAsync(CancellationToken ct = default) => Task.FromResult<IShippingProvider?>(new FailingProvider());
    }

    [Fact]
    public async Task Falha_do_provedor_mantem_a_retirada_e_avisa_o_comprador()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var part = await api.CreatePartAsync(owner);
        await api.ConfigureShippingAsync(owner, tenant, "01310100", pickup: true, "Balcão");
        using var failing = api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddScoped<IShippingProviderResolver, FailingResolver>()));
        var client = failing.CreateClient();
        client.DefaultRequestHeaders.Host = $"{tenant.Slug}.plataforma.test";

        var quote = await (await client.PostAsJsonAsync("/api/loja/frete", new { cep = "88015200", items = Items((part, 1)) })).Content.ReadFromJsonAsync<QuoteDto>();

        Assert.Empty(quote!.Options);
        Assert.Equal("Balcão", quote.Pickup);
        Assert.Equal("Não foi possível calcular o frete agora. Tente de novo em instantes.", quote.Message);
    }
}
