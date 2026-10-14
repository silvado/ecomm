using System.Net;
using System.Net.Http.Json;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF03 — domínio próprio: só nos planos com o recurso; registros exatos (CNAME/A + TXT de verificação); verificado vira
/// o principal, entra na lista do Caddy e abre a loja; remover tira na hora; um domínio é de uma loja só.
/// </summary>
public sealed class CustomDomainTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record RecordDto(string Type, string Name, string Value, bool Alternative);
    private sealed record DomainDto(Guid Id, string Host, string Kind, string Status, bool IsPrimary, string? LastCheckMessage, List<RecordDto> Records);
    private sealed record DomainsDto(bool Allowed, string PlatformHost, List<DomainDto> Domains);
    private sealed record PublicDto(string Slug, string? PrimaryHost);
    private sealed record ProblemDto(string Title);

    private static string NewHost(string prefix = "www.") => $"{prefix}loja{Guid.NewGuid().ToString("N")[..8]}.com.br";

    private async Task<(Tenant Tenant, string Owner)> ShopAsync(string plan = Plan.Professional)
    {
        var tenant = await api.CreateTenantAsync(plan);
        return (tenant, await api.SessionForAsync(tenant));
    }

    private async Task<DomainsDto> AddAsync(string owner, string host)
    {
        var response = await SendAsync(api.PanelClient(), owner, HttpMethod.Post, "/api/painel/loja/dominios", new { dominio = host });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DomainsDto>())!;
    }

    private Task<HttpResponseMessage> AskAsync(string host) => api.ClientFor("api:8080", remoteIp: "10.0.0.2").GetAsync($"/internal/tls/ask?domain={host}");

    private static void PointCorrectly(Tenant tenant, DomainDto domain)
    {
        FakeDnsResolver.Set(domain.Host, "CNAME", $"{tenant.Slug}.plataforma.test");
        var txt = domain.Records.Single(r => r.Type == "TXT");
        FakeDnsResolver.Set(txt.Name, "TXT", txt.Value);
    }

    private async Task<DomainsDto> VerifyAsync(string owner, Guid id) =>
        (await (await SendAsync(api.PanelClient(), owner, HttpMethod.Post, $"/api/painel/loja/dominios/{id}/verificar")).Content.ReadFromJsonAsync<DomainsDto>())!;

    [Fact]
    public async Task Plano_sem_o_recurso_nao_cadastra()
    {
        var (_, owner) = await ShopAsync(Plan.Essential);
        var list = (await (await GetAsync(api.PanelClient(), owner, "/api/painel/loja/dominios")).Content.ReadFromJsonAsync<DomainsDto>())!;
        Assert.False(list.Allowed);
        var refused = await SendAsync(api.PanelClient(), owner, HttpMethod.Post, "/api/painel/loja/dominios", new { dominio = NewHost() });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("não inclui domínio próprio", (await refused.Content.ReadFromJsonAsync<ProblemDto>())!.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Com_cname_e_txt_certos_verifica_vira_principal_entra_no_caddy_e_abre_a_loja()
    {
        var (tenant, owner) = await ShopAsync();
        var host = NewHost();
        var domain = (await AddAsync(owner, host)).Domains.Single();
        Assert.Equal(("pending", "customWww", false), (domain.Status, domain.Kind, domain.IsPrimary));
        Assert.Contains(domain.Records, r => r is { Type: "CNAME", Value: var v } && v == $"{tenant.Slug}.plataforma.test");
        var txt = domain.Records.Single(r => r.Type == "TXT");
        Assert.Equal($"_ecomm-verificacao.{host}", txt.Name);
        Assert.StartsWith("ecomm-verificacao=", txt.Value, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await AskAsync(host)).StatusCode);

        // Só o CNAME: falta a prova.
        FakeDnsResolver.Set(host, "CNAME", $"{tenant.Slug}.plataforma.test");
        var half = (await VerifyAsync(owner, domain.Id)).Domains.Single();
        Assert.Equal("pending", half.Status);
        Assert.Contains("Falta o registro TXT", half.LastCheckMessage, StringComparison.Ordinal);

        PointCorrectly(tenant, domain);
        var verified = (await VerifyAsync(owner, domain.Id)).Domains.Single();
        Assert.Equal(("verified", true), (verified.Status, verified.IsPrimary));
        Assert.Equal(HttpStatusCode.OK, (await AskAsync(host)).StatusCode);

        // A loja abre pelo domínio próprio e o subdomínio sabe para onde redirecionar.
        var viaCustom = await api.ClientFor(host).GetFromJsonAsync<PublicDto>("/api/loja/identidade");
        Assert.Equal((tenant.Slug, host), (viaCustom!.Slug, viaCustom.PrimaryHost));
    }

    [Fact]
    public async Task Apontado_para_outro_lugar_explica_o_problema_e_raiz_aceita_registro_a()
    {
        var (tenant, owner) = await ShopAsync();
        var www = (await AddAsync(owner, NewHost())).Domains.Single();
        FakeDnsResolver.Set(www.Host, "CNAME", "outro-provedor.exemplo.com");
        var wrong = (await VerifyAsync(owner, www.Id)).Domains.Single();
        Assert.Contains("aponta para outro-provedor.exemplo.com", wrong.LastCheckMessage, StringComparison.Ordinal);

        var apexHost = NewHost(prefix: "");
        var apex = (await AddAsync(owner, apexHost)).Domains.Single(d => d.Host == apexHost);
        Assert.Equal("customApex", apex.Kind);
        Assert.Contains(apex.Records, r => r is { Type: "A", Value: "203.0.113.50", Alternative: true });
        FakeDnsResolver.Set(apexHost, "A", "203.0.113.50");
        var txt = apex.Records.Single(r => r.Type == "TXT");
        FakeDnsResolver.Set(txt.Name, "TXT", txt.Value);
        var view = await VerifyAsync(owner, apex.Id);
        Assert.Equal(("verified", true), (view.Domains.Single(d => d.Host == apexHost).Status, view.Domains.Single(d => d.Host == apexHost).IsPrimary));
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(api.PanelClient(), owner, HttpMethod.Post, "/api/painel/loja/dominios", new { dominio = NewHost(prefix: "") })).StatusCode);
        _ = tenant;
    }

    [Fact]
    public async Task Verificacao_periodica_confirma_e_desiste_depois_de_72_horas()
    {
        var (tenant, owner) = await ShopAsync();
        var good = (await AddAsync(owner, NewHost())).Domains.Single();
        var (other, otherOwner) = await ShopAsync();
        var bad = (await AddAsync(otherOwner, NewHost())).Domains.Single();
        PointCorrectly(tenant, good);
        await api.WithPlatformAsync(db => db.Database.ExecuteSqlAsync($"UPDATE tenant_domains SET requested_at = now() - interval '73 hours' WHERE id = {bad.Id}"));

        var job = ActivatorUtilities.CreateInstance<DomainVerificationJob>(api.Services);
        Assert.True(await job.CheckPendingAsync(CancellationToken.None) >= 1);

        var goodNow = (await (await GetAsync(api.PanelClient(), owner, "/api/painel/loja/dominios")).Content.ReadFromJsonAsync<DomainsDto>())!.Domains.Single();
        var badNow = (await (await GetAsync(api.PanelClient(), otherOwner, "/api/painel/loja/dominios")).Content.ReadFromJsonAsync<DomainsDto>())!.Domains.Single();
        Assert.Equal("verified", goodNow.Status);
        Assert.Equal("failed", badNow.Status);

        // Verificar de novo reabre a janela.
        Assert.Equal("pending", (await VerifyAsync(otherOwner, bad.Id)).Domains.Single().Status);
        _ = other;
    }

    [Fact]
    public async Task Remover_tira_do_caddy_na_hora_e_o_subdominio_volta_a_ser_o_principal()
    {
        var (tenant, owner) = await ShopAsync();
        var domain = (await AddAsync(owner, NewHost())).Domains.Single();
        PointCorrectly(tenant, domain);
        await VerifyAsync(owner, domain.Id);
        Assert.Equal(HttpStatusCode.OK, (await AskAsync(domain.Host)).StatusCode);
        // Deixa a identidade pública em cache apontando para o domínio próprio: remover precisa limpá-la.
        var before = await api.StoreClient(tenant).GetFromJsonAsync<PublicDto>("/api/loja/identidade");
        Assert.Equal(domain.Host, before!.PrimaryHost);

        var after = (await (await SendAsync(api.PanelClient(), owner, HttpMethod.Delete, $"/api/painel/loja/dominios/{domain.Id}")).Content.ReadFromJsonAsync<DomainsDto>())!;
        Assert.Empty(after.Domains);
        Assert.Equal(HttpStatusCode.NotFound, (await AskAsync(domain.Host)).StatusCode);
        var viaSubdomain = await api.StoreClient(tenant).GetFromJsonAsync<PublicDto>("/api/loja/identidade");
        Assert.Equal($"{tenant.Slug}.plataforma.test", viaSubdomain!.PrimaryHost);
    }

    [Fact]
    public async Task Dominio_e_de_uma_loja_so_e_operador_nao_mexe()
    {
        var (_, owner) = await ShopAsync();
        var (other, otherOwner) = await ShopAsync();
        var host = NewHost();
        await AddAsync(owner, host);
        var taken = await SendAsync(api.PanelClient(), otherOwner, HttpMethod.Post, "/api/painel/loja/dominios", new { dominio = host.ToUpperInvariant() });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);

        var invalid = await SendAsync(api.PanelClient(), otherOwner, HttpMethod.Post, "/api/painel/loja/dominios", new { dominio = "http://loja .com/x" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var platform = await SendAsync(api.PanelClient(), otherOwner, HttpMethod.Post, "/api/painel/loja/dominios", new { dominio = "outra.plataforma.test" });
        Assert.Equal(HttpStatusCode.BadRequest, platform.StatusCode);

        var operatorToken = await api.SessionForAsync(other, TenantRole.Operator);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(api.PanelClient(), operatorToken, "/api/painel/loja/dominios")).StatusCode);
    }
}
