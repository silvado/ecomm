using System.Net;
using System.Net.Http.Json;
using Ecommerce.Domain.Platform;
using Ecommerce.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>RF40 — loja personalizada: a API recusa mudar a aparência; frete e estoque continuam com o Dono.</summary>
public sealed class CustomStorefrontTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record ThemeDto(string PrimaryColor, string BackgroundColor, string TextColor);
    private sealed record TextsDto(string About, string ReturnPolicy, string Footer);
    private sealed record ShippingDto(string? OriginPostalCode, bool PickupEnabled, string? PickupAddress);
    private sealed record ProfileDto(string TradeName, ThemeDto Theme, TextsDto Texts, bool HideOutOfStock, ShippingDto Shipping, bool CustomStorefront);
    private sealed record ProblemDto(string Title);

    private Task SetCustomAsync(Tenant tenant, bool enabled) => api.WithPlatformAsync(async db =>
    {
        var row = await db.Tenants.SingleAsync(t => t.Id == tenant.Id);
        row.SetCustomStorefront(enabled, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
    });

    private static object Body(ProfileDto p, string? primary = null, string? footer = null, bool? hide = null, string? origin = null) => new
    {
        tradeName = p.TradeName, primaryColor = primary ?? p.Theme.PrimaryColor.ToLowerInvariant(), backgroundColor = p.Theme.BackgroundColor,
        textColor = p.Theme.TextColor, about = p.Texts.About, returnPolicy = p.Texts.ReturnPolicy, footer = footer ?? p.Texts.Footer + "  ",
        hideOutOfStock = hide ?? p.HideOutOfStock,
        shipping = new { originPostalCode = origin ?? p.Shipping.OriginPostalCode, pickupEnabled = p.Shipping.PickupEnabled, pickupAddress = p.Shipping.PickupAddress },
    };

    [Fact]
    public async Task Ligada_recusa_aparencia_e_logo_mas_aceita_frete_e_estoque()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        await SetCustomAsync(tenant, true);

        var profile = (await (await GetAsync(api.PanelClient(), owner, "/api/painel/loja")).Content.ReadFromJsonAsync<ProfileDto>())!;
        Assert.True(profile.CustomStorefront);

        // O painel reenvia a aparência como leu (com diferenças de espaço e maiúsculas): passa, e frete/estoque mudam.
        var ok = await SendAsync(api.PanelClient(), owner, HttpMethod.Put, "/api/painel/loja", Body(profile, hide: true, origin: "01310-100"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var saved = (await ok.Content.ReadFromJsonAsync<ProfileDto>())!;
        Assert.Equal((true, "01310100"), (saved.HideOutOfStock, saved.Shipping.OriginPostalCode));

        var color = await SendAsync(api.PanelClient(), owner, HttpMethod.Put, "/api/painel/loja", Body(profile, primary: "#B42318"));
        Assert.Equal(HttpStatusCode.Conflict, color.StatusCode);
        Assert.Contains("Loja personalizada", (await color.Content.ReadFromJsonAsync<ProblemDto>())!.Title, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(api.PanelClient(), owner, HttpMethod.Put, "/api/painel/loja", Body(profile, footer: "Outro rodapé"))).StatusCode);

        using var logo = new HttpRequestMessage(HttpMethod.Put, "/api/painel/loja/logo") { Content = new ByteArrayContent(TestImages.Jpeg(64, 64)) };
        logo.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", owner);
        Assert.Equal(HttpStatusCode.Conflict, (await api.PanelClient().SendAsync(logo)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(api.PanelClient(), owner, HttpMethod.Delete, "/api/painel/loja/logo")).StatusCode);

        var after = (await (await GetAsync(api.PanelClient(), owner, "/api/painel/loja")).Content.ReadFromJsonAsync<ProfileDto>())!;
        Assert.Equal(profile.Theme, after.Theme);
        Assert.Equal(profile.Texts, after.Texts);
    }

    [Fact]
    public async Task Desligada_devolve_a_edicao_ao_dono()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        await SetCustomAsync(tenant, true);
        await SetCustomAsync(tenant, false);

        var profile = (await (await GetAsync(api.PanelClient(), owner, "/api/painel/loja")).Content.ReadFromJsonAsync<ProfileDto>())!;
        Assert.False(profile.CustomStorefront);
        var saved = await SendAsync(api.PanelClient(), owner, HttpMethod.Put, "/api/painel/loja", Body(profile, primary: "#B42318"));
        Assert.Equal("#B42318", (await saved.Content.ReadFromJsonAsync<ProfileDto>())!.Theme.PrimaryColor);
    }
}
