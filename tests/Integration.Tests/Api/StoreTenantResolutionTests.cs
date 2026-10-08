using System.Net;
using System.Net.Http.Json;
using Ecommerce.Api.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>RF04 — resolução do tenant pelo Host em toda requisição da loja.</summary>
public sealed class StoreTenantResolutionTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record Identity(string Slug, string Name);

    [Fact]
    public async Task Subdominio_da_plataforma_resolve_a_loja()
    {
        var response = await api.ClientFor("ativa.plataforma.test").GetAsync("/api/loja/identidade");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var identity = await response.Content.ReadFromJsonAsync<Identity>();
        Assert.Equal(new Identity("ativa", "Loja Ativa"), identity);
    }

    [Fact]
    public async Task Host_desconhecido_retorna_404_generico()
    {
        var response = await api.ClientFor("outra.plataforma.test").GetAsync("/api/loja/identidade");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("Loja não encontrada.", problem!.Title);
    }

    [Fact]
    public async Task Loja_suspensa_retorna_503_com_tipo_para_o_storefront()
    {
        var response = await api.ClientFor("suspensa.plataforma.test").GetAsync("/api/loja/identidade");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(StoreProblemTypes.StoreUnavailable, problem!.Type);
    }

    [Fact]
    public async Task X_Forwarded_Host_de_origem_nao_confiavel_e_ignorado()
    {
        var client = api.ClientFor("outra.plataforma.test", remoteIp: "203.0.113.10");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.7");
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "ativa.plataforma.test");

        var response = await client.GetAsync("/api/loja/identidade");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task X_Forwarded_Host_do_proxy_interno_e_usado()
    {
        // storefront SSR (rede interna) chamando a API em nome do comprador
        var client = api.ClientFor("api:8080", remoteIp: "10.0.0.5");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.7");
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "ativa.plataforma.test");

        var response = await client.GetAsync("/api/loja/identidade");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("ativa.plataforma.test", HttpStatusCode.OK)]
    [InlineData("suspensa.plataforma.test", HttpStatusCode.NotFound)]
    [InlineData("desconhecido.com.br", HttpStatusCode.NotFound)]
    public async Task Ask_do_Caddy_autoriza_somente_dominio_verificado_de_loja_disponivel(string domain, HttpStatusCode expected)
    {
        var response = await api.ClientFor("api:8080", remoteIp: "10.0.0.2").GetAsync($"/internal/tls/ask?domain={domain}");

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Endpoint_interno_recusa_origem_externa()
    {
        var response = await api.ClientFor("api.plataforma.test", remoteIp: "203.0.113.10")
            .GetAsync("/internal/tls/ask?domain=ativa.plataforma.test");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Endpoint_interno_recusa_requisicao_que_passou_pelo_proxy_publico()
    {
        var client = api.ClientFor("api.plataforma.test", remoteIp: "10.0.0.2");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.7");

        var response = await client.GetAsync("/internal/tls/ask?domain=ativa.plataforma.test");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
