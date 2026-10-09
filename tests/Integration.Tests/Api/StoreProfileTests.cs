using System.Net;
using System.Net.Http.Json;
using Ecommerce.Domain.Identity;
using Microsoft.AspNetCore.Mvc;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>RF01 — dados, aparência e textos da loja, e o reflexo na loja pública.</summary>
public sealed class StoreProfileTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record ThemeDto(string PrimaryColor, string OnPrimaryColor, string BackgroundColor, string TextColor);
    private sealed record TextsDto(string About, string ReturnPolicy, string Footer);
    private sealed record ProfileDto(string Slug, string Cnpj, string LegalName, string TradeName, ThemeDto Theme, TextsDto Texts, List<string> Warnings);
    private sealed record PublicDto(string Slug, string Name, ThemeDto Theme, TextsDto Texts);

    private static object Update(string name = "Loja Renovada", string primary = "#B42318", string background = "#FFFFFF",
        string text = "#101828", string about = "Desde 1998.") =>
        new { tradeName = name, primaryColor = primary, backgroundColor = background, textColor = text, about, returnPolicy = "7 dias.", footer = "Rua A, 1" };

    [Fact]
    public async Task Loja_nova_tem_aparencia_padrao_sem_avisos()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);

        var profile = await (await GetAsync(api.PanelClient(), owner, "/api/painel/loja")).Content.ReadFromJsonAsync<ProfileDto>();

        Assert.Equal(tenant.Cnpj.Value, profile!.Cnpj);
        Assert.Equal("#FFFFFF", profile.Theme.BackgroundColor);
        Assert.Empty(profile.Warnings);
    }

    [Fact]
    public async Task Dono_altera_nome_cores_e_textos_e_a_loja_publica_reflete_na_hora()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var store = api.ClientFor($"{tenant.Slug}.plataforma.test");
        await store.GetFromJsonAsync<PublicDto>("/api/loja/identidade"); // aquece o cache

        var saved = await SendAsync(api.PanelClient(), owner, HttpMethod.Put, "/api/painel/loja", Update());
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var after = await store.GetFromJsonAsync<PublicDto>("/api/loja/identidade");
        Assert.Equal("Loja Renovada", after!.Name);
        Assert.Equal(new ThemeDto("#B42318", "#FFFFFF", "#FFFFFF", "#101828"), after.Theme);
        Assert.Equal(new TextsDto("Desde 1998.", "7 dias.", "Rua A, 1"), after.Texts);
    }

    [Fact]
    public async Task Contraste_ruim_grava_com_aviso()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);

        var saved = await SendAsync(api.PanelClient(), owner, HttpMethod.Put, "/api/painel/loja", Update(text: "#CCCCCC"));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(["textOnBackground"], (await saved.Content.ReadFromJsonAsync<ProfileDto>())!.Warnings);
    }

    [Theory]
    [InlineData("vermelho", "Cor inválida: use o formato #RRGGBB.")]
    [InlineData("#12345", "Cor inválida: use o formato #RRGGBB.")]
    public async Task Cor_invalida_e_recusada_sem_gravar_nada(string color, string message)
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);

        var refused = await SendAsync(api.PanelClient(), owner, HttpMethod.Put, "/api/painel/loja", Update(name: "Nome Novo", primary: color));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(message, (await refused.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
        var profile = await (await GetAsync(api.PanelClient(), owner, "/api/painel/loja")).Content.ReadFromJsonAsync<ProfileDto>();
        Assert.Equal(tenant.TradeName, profile!.TradeName);
    }

    [Fact]
    public async Task Operador_nao_ve_nem_altera_os_dados_da_loja()
    {
        var tenant = await api.CreateTenantAsync();
        var op = await api.SessionForAsync(tenant, TenantRole.Operator);

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(api.PanelClient(), op, "/api/painel/loja")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(api.PanelClient(), op, HttpMethod.Put, "/api/painel/loja", Update())).StatusCode);
    }

    [Fact]
    public async Task Alterar_uma_loja_nao_muda_outra()
    {
        var (a, b) = (await api.CreateTenantAsync(), await api.CreateTenantAsync());
        var ownerA = await api.SessionForAsync(a);
        await SendAsync(api.PanelClient(), ownerA, HttpMethod.Put, "/api/painel/loja", Update(primary: "#067647"));
        var storeA = await api.ClientFor($"{a.Slug}.plataforma.test").GetFromJsonAsync<PublicDto>("/api/loja/identidade");

        var storeB = await api.ClientFor($"{b.Slug}.plataforma.test").GetFromJsonAsync<PublicDto>("/api/loja/identidade");

        Assert.Equal("#067647", storeA!.Theme.PrimaryColor);

        Assert.Equal(b.TradeName, storeB!.Name);
        Assert.Equal("#1F5FBF", storeB.Theme.PrimaryColor);
    }

    [Fact]
    public async Task Primeiras_gravacoes_simultaneas_nao_falham()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => Task.Run(() =>
            SendAsync(api.PanelClient(), owner, HttpMethod.Put, "/api/painel/loja", Update(about: $"versão {i}")))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }
}
