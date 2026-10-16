using System.Net;
using System.Net.Http.Json;
using Ecommerce.Application.Tenancy;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF05 — primeiros passos: loja nova nasce em implantação ("em breve"), cada passo segue as configurações da loja, só os
/// opcionais podem ser pulados, canais conforme o plano e publicar exige empresa, pagamento e frete.
/// </summary>
public sealed class OnboardingTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record StepDto(string Step, bool Required, bool Done, bool Skipped, string? Detail);
    private sealed record ViewDto(bool Published, List<StepDto> Steps, bool CanPublish);
    private sealed record ProblemDto(string Title, string? Type);

    private const string ComingSoon = "https://plataforma/problemas/loja-em-breve";

    [Fact]
    public async Task Loja_criada_pelo_comando_nasce_em_implantacao_com_a_vitrine_em_breve()
    {
        await using var scope = api.Services.CreateAsyncScope();
        var slug = "ob" + Guid.NewGuid().ToString("N")[..8];
        var created = await scope.ServiceProvider.GetRequiredService<ITenantProvisioning>().CreateAsync(
            new CreateTenantRequest(slug, Cnpjs.Random().Value, "Peças OB Ltda", "Peças OB", $"dono@{slug}.test"));
        var tenant = await api.WithPlatformAsync(db => db.Tenants.AsNoTracking().SingleAsync(t => t.Id == created.Tenant!.TenantId));

        Assert.Equal(TenantStatus.Onboarding, tenant.Status);
        var response = await api.StoreClient(tenant).GetAsync("/api/loja/identidade");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(ComingSoon, (await response.Content.ReadFromJsonAsync<ProblemDto>())!.Type);
    }

    [Fact]
    public async Task Passos_seguem_as_configuracoes_e_publicar_exige_empresa_pagamento_e_frete()
    {
        var tenant = await api.CreateTenantAsync(published: false);
        var owner = await api.SessionForAsync(tenant);
        var client = api.PanelClient();

        var view = await ViewAsync(client, owner);
        Assert.False(view.Published);
        // Essencial: sem domínio próprio, Instagram nem OLX (RF05 CA2).
        Assert.Equal(["company", "payment", "fiscal", "shipping", "mercadoLivre"], view.Steps.Select(s => s.Step).ToList());
        Assert.Equal(["company", "payment", "shipping"], view.Steps.Where(s => s.Required).Select(s => s.Step).ToList());
        // Ambiente de teste: o pagamento simulado conta como configurado.
        Assert.True(view.Steps.Single(s => s.Step == "payment").Done);
        Assert.False(view.CanPublish);

        var refused = await SendAsync(client, owner, HttpMethod.Post, "/api/painel/primeiros-passos/publicar", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("dados da loja, frete", (await refused.Content.ReadFromJsonAsync<ProblemDto>())!.Title, StringComparison.Ordinal);

        // Salvar Dados da loja com o CEP conclui empresa e frete de uma vez.
        var profile = await (await GetAsync(client, owner, "/api/painel/loja")).Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>();
        var saved = await SendAsync(client, owner, HttpMethod.Put, "/api/painel/loja", new
        {
            tradeName = (string)profile!["tradeName"]!, primaryColor = "#b42318", backgroundColor = "#ffffff", textColor = "#101828",
            about = "Peças usadas com garantia.", returnPolicy = "", footer = "",
            shipping = new { originPostalCode = "01310-100", pickupEnabled = false, pickupAddress = (string?)null },
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        view = await ViewAsync(client, owner);
        Assert.True(view.CanPublish);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await api.StoreClient(tenant).GetAsync("/api/loja/identidade")).StatusCode);

        var published = await SendAsync(client, owner, HttpMethod.Post, "/api/painel/primeiros-passos/publicar", null);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        Assert.True((await published.Content.ReadFromJsonAsync<ViewDto>())!.Published);
        // Catálogo invalidado: a vitrine abre na hora.
        Assert.Equal(HttpStatusCode.OK, (await api.StoreClient(tenant).GetAsync("/api/loja/identidade")).StatusCode);
        Assert.Equal(TenantStatus.Active, await api.WithPlatformAsync(db => db.Tenants.Where(t => t.Id == tenant.Id).Select(t => t.Status).SingleAsync()));
        // Publicar de novo não muda nada.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, owner, HttpMethod.Post, "/api/painel/primeiros-passos/publicar", null)).StatusCode);
    }

    [Fact]
    public async Task So_opcionais_podem_ser_pulados_e_cada_loja_ve_os_seus()
    {
        var tenant = await api.CreateTenantAsync(planCode: Plan.Complete, published: false);
        var other = await api.CreateTenantAsync(planCode: Plan.Complete, published: false);
        var owner = await api.SessionForAsync(tenant);
        var otherOwner = await api.SessionForAsync(other);
        var client = api.PanelClient();

        var view = await ViewAsync(client, owner);
        Assert.Equal(["company", "domain", "payment", "fiscal", "shipping", "mercadoLivre", "instagram", "olx"], view.Steps.Select(s => s.Step).ToList());

        var skipped = await SendAsync(client, owner, HttpMethod.Put, "/api/painel/primeiros-passos/olx/pular", null);
        Assert.Equal(HttpStatusCode.OK, skipped.StatusCode);
        Assert.True((await skipped.Content.ReadFromJsonAsync<ViewDto>())!.Steps.Single(s => s.Step == "olx").Skipped);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, owner, HttpMethod.Put, "/api/painel/primeiros-passos/olx/pular", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(client, owner, HttpMethod.Put, "/api/painel/primeiros-passos/shipping/pular", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(client, owner, HttpMethod.Put, "/api/painel/primeiros-passos/inexistente/pular", null)).StatusCode);

        // Isolamento: o pulo de uma loja não aparece na outra.
        Assert.False((await ViewAsync(client, otherOwner)).Steps.Single(s => s.Step == "olx").Skipped);

        var back = await SendAsync(client, owner, HttpMethod.Delete, "/api/painel/primeiros-passos/olx/pular", null);
        Assert.False((await back.Content.ReadFromJsonAsync<ViewDto>())!.Steps.Single(s => s.Step == "olx").Skipped);
    }

    [Fact]
    public async Task Operador_nao_ve_nem_publica()
    {
        var tenant = await api.CreateTenantAsync(published: false);
        var token = await api.SessionForAsync(tenant, TenantRole.Operator);

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(api.PanelClient(), token, "/api/painel/primeiros-passos")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(api.PanelClient(), token, HttpMethod.Post, "/api/painel/primeiros-passos/publicar", null)).StatusCode);
    }

    [Fact]
    public async Task Loja_suspensa_nao_se_publica_sozinha()
    {
        var tenant = await api.CreateTenantAsync(published: false);
        var owner = await api.SessionForAsync(tenant);
        await api.WithPlatformAsync(async db =>
        {
            var stored = await db.Tenants.SingleAsync(t => t.Id == tenant.Id);
            stored.Suspend();
            await db.SaveChangesAsync();
        });

        var response = await SendAsync(api.PanelClient(), owner, HttpMethod.Post, "/api/painel/primeiros-passos/publicar", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Certificado_vale_para_loja_em_implantacao()
    {
        var tenant = await api.CreateTenantAsync(published: false);

        var response = await api.ClientFor("api:8080", remoteIp: "10.0.0.2").GetAsync($"/internal/tls/ask?domain={tenant.Slug}.plataforma.test");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<ViewDto> ViewAsync(HttpClient client, string token) =>
        (await (await PanelHttp.GetAsync(client, token, "/api/painel/primeiros-passos")).Content.ReadFromJsonAsync<ViewDto>())!;
}
