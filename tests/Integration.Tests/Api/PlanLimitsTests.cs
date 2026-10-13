using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Channels;
using Ecommerce.Infrastructure.Tenancy;
using Ecommerce.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF37 — planos como dados: limite de peças ativas (inclusive em ativações simultâneas), recurso fora do plano, troca de
/// plano com efeito imediato e baixa de plano sem desativar o que já existe.
/// </summary>
public sealed class PlanLimitsTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record UsageDto(string Key, int Used, int? Limit);
    private sealed record PlanDto(string Code, string Name, List<UsageDto> Usage, List<string> Features);
    private sealed record ProblemDto(string Title);
    private sealed record PartDto(Guid Id, string Status);

    /// <summary>Plano de teste com poucas peças ativas (os planos reais têm 500/3.000).</summary>
    private async Task<string> SmallPlanAsync(int activeParts)
    {
        var code = "teste-" + Guid.NewGuid().ToString("N")[..8];
        await api.WithPlatformAsync(async db =>
        {
            db.Plans.Add(new Plan(Guid.CreateVersion7(), code, "Teste"));
            db.PlanLimits.Add(new PlanLimit(code, PlanLimit.ActiveParts, activeParts));
            db.PlanLimits.Add(new PlanLimit(code, PlanLimit.Users, 5));
            await db.SaveChangesAsync();
        });
        return code;
    }

    private Task SetPlanAsync(Tenant tenant, string code) => api.WithPlatformAsync(async db =>
    {
        var row = await db.Tenants.SingleAsync(t => t.Id == tenant.Id);
        row.ChangePlan(code);
        await db.SaveChangesAsync();
    });

    /// <summary>Peça com foto, em rascunho: pronta para ativar.</summary>
    private async Task<Guid> DraftWithPhotoAsync(string owner)
    {
        var id = await api.CreatePartAsync(owner, active: false);
        using var photo = new HttpRequestMessage(HttpMethod.Post, $"/api/painel/pecas/{id}/fotos") { Content = new ByteArrayContent(TestImages.Jpeg(120, 90)) };
        photo.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner);
        await api.PanelClient().SendAsync(photo);
        return id;
    }

    private Task<HttpResponseMessage> ActivateAsync(string owner, Guid part) =>
        SendAsync(api.PanelClient(), owner, HttpMethod.Put, $"/api/painel/pecas/{part}/situacao", new { status = "active" });

    [Fact]
    public async Task Limite_de_pecas_ativas_recusa_a_proxima_e_troca_de_plano_vale_na_hora()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        await SetPlanAsync(tenant, await SmallPlanAsync(2));
        var parts = new List<Guid>();
        for (var i = 0; i < 3; i++) parts.Add(await DraftWithPhotoAsync(owner));

        Assert.Equal(HttpStatusCode.OK, (await ActivateAsync(owner, parts[0])).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ActivateAsync(owner, parts[1])).StatusCode);
        var refused = await ActivateAsync(owner, parts[2]);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("permite até 2 peças ativas", (await refused.Content.ReadFromJsonAsync<ProblemDto>())!.Title, StringComparison.Ordinal);

        // Reativar uma que já está ativa não conta de novo.
        Assert.Equal(HttpStatusCode.OK, (await ActivateAsync(owner, parts[0])).StatusCode);

        await SetPlanAsync(tenant, Plan.Complete); // sem limite de peças
        Assert.Equal(HttpStatusCode.OK, (await ActivateAsync(owner, parts[2])).StatusCode);
    }

    [Fact]
    public async Task Ativacoes_simultaneas_nao_passam_do_limite()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        await SetPlanAsync(tenant, await SmallPlanAsync(1));
        var parts = new List<Guid>();
        for (var i = 0; i < 4; i++) parts.Add(await DraftWithPhotoAsync(owner));

        var responses = await Task.WhenAll(parts.Select(p => ActivateAsync(owner, p)));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenant.Id);
        var db = scope.ServiceProvider.GetRequiredService<Ecommerce.Infrastructure.Persistence.TenantDbContext>();
        Assert.Equal(1, await db.Parts.CountAsync(p => p.Status == Ecommerce.Domain.Catalog.PartStatus.Active));
    }

    [Fact]
    public async Task Baixar_de_plano_mantem_o_que_ja_esta_ativo_e_barra_novas_ativacoes()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        await SetPlanAsync(tenant, Plan.Complete);
        var parts = new List<Guid>();
        for (var i = 0; i < 4; i++) parts.Add(await DraftWithPhotoAsync(owner));
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await ActivateAsync(owner, parts[i])).StatusCode);

        await SetPlanAsync(tenant, await SmallPlanAsync(2));
        var plan = (await (await GetAsync(api.PanelClient(), owner, "/api/painel/plano")).Content.ReadFromJsonAsync<PlanDto>())!;
        Assert.Equal((3, (int?)2), (plan.Usage.Single(u => u.Key == "active_parts").Used, plan.Usage.Single(u => u.Key == "active_parts").Limit));
        Assert.Equal(HttpStatusCode.BadRequest, (await ActivateAsync(owner, parts[3])).StatusCode);
        Assert.Equal("active", (await (await GetAsync(api.PanelClient(), owner, $"/api/painel/pecas/{parts[0]}")).Content.ReadFromJsonAsync<PartDto>())!.Status);
    }

    [Fact]
    public async Task Cartao_seu_plano_mostra_limites_e_recursos_do_plano()
    {
        var tenant = await api.CreateTenantAsync(Plan.Essential);
        var owner = await api.SessionForAsync(tenant);
        var plan = (await (await GetAsync(api.PanelClient(), owner, "/api/painel/plano")).Content.ReadFromJsonAsync<PlanDto>())!;
        Assert.Equal(("essencial", "Essencial"), (plan.Code, plan.Name));
        Assert.Equal(500, plan.Usage.Single(u => u.Key == "active_parts").Limit);
        Assert.Equal((1, (int?)2), (plan.Usage.Single(u => u.Key == "users").Used, plan.Usage.Single(u => u.Key == "users").Limit));
        Assert.Contains(PlanFeature.MercadoLivre, plan.Features);
        Assert.DoesNotContain(PlanFeature.Meta, plan.Features);

        var operatorToken = await api.SessionForAsync(tenant, Ecommerce.Domain.Identity.TenantRole.Operator);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(api.PanelClient(), operatorToken, "/api/painel/plano")).StatusCode);
    }

    [Fact]
    public async Task Canal_fora_do_plano_nao_conecta()
    {
        var tenant = await api.CreateTenantAsync(Plan.Essential);
        async Task ConnectAsync(string channel)
        {
            await using var scope = api.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenant.Id);
            await scope.ServiceProvider.GetRequiredService<ChannelService>().ConnectAsync(channel, "conta-" + Guid.NewGuid().ToString("N")[..8]);
        }

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => ConnectAsync("meta"));
        Assert.Contains("O plano Essencial não inclui", refused.Message, StringComparison.Ordinal);
        await ConnectAsync("mercado-livre");

        await SetPlanAsync(tenant, Plan.Professional);
        await ConnectAsync("meta");
    }
}
