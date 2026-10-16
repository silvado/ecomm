using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Ecommerce.Application.Ai;
using Ecommerce.Application.Privacy;
using Ecommerce.Domain.Ai;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Ai;
using Ecommerce.Infrastructure.Billing;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF35/RF36 — base da IA: toda chamada registrada (também as que falham), contador mensal por loja e funcionalidade, só
/// sucesso conta, limite do plano atômico sob concorrência e isolamento entre lojas.
/// </summary>
public sealed class AiCompletionTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record UsageDto(string Key, int Used, int? Limit);
    private sealed record PlanDto(List<UsageDto> Usage);

    private static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["titulo"] = new JsonObject { ["type"] = "string" },
            ["condicao"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("usada", "nova") },
            ["codigos"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
        },
        ["required"] = new JsonArray("titulo", "condicao", "codigos"),
        ["additionalProperties"] = false,
    };

    private static AiRequest Request(string feature = AiFeature.PartFromPhotos, string prompt = "Código interno P-10. Descreva a peça.") =>
        new(feature, "Você cadastra autopeças usadas.", prompt, [new AiImage("image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF })], Schema,
            "1 foto, código P-10");

    /// <summary>Chama a IA como o painel faria: loja e usuário no escopo.</summary>
    private async Task<AiResult> CallAsync(Guid tenantId, AiRequest request, Guid? userId = null)
    {
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        if (userId is { } user) scope.ServiceProvider.GetRequiredService<PanelActor>().UserId = user;
        return await scope.ServiceProvider.GetRequiredService<IAiCompletion>().CompleteAsync(request);
    }

    private async Task<T> InTenantAsync<T>(Guid tenantId, Func<TenantDbContext, Task<T>> read)
    {
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        return await read(scope.ServiceProvider.GetRequiredService<TenantDbContext>());
    }

    private Task<int> UsedAsync(Guid tenantId, string feature = AiFeature.PartFromPhotos) => InTenantAsync(tenantId, db =>
        db.AiUsageCounters.Where(c => c.Feature == feature).Select(c => c.Used).SingleOrDefaultAsync());

    private Task<List<AiInteraction>> InteractionsAsync(Guid tenantId) => InTenantAsync(tenantId, db => db.AiInteractions.AsNoTracking().OrderBy(i => i.At).ToListAsync());

    [Fact]
    public async Task Chamada_bem_sucedida_e_registrada_e_conta_no_plano()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var userId = Guid.NewGuid();

        var result = await CallAsync(tenant.Id, Request(), userId);

        Assert.Equal(FakeAiCompletion.Model, result.Model);
        Assert.Equal("usada", result.Output.GetProperty("condicao").GetString());
        Assert.Equal(0, result.Output.GetProperty("codigos").GetArrayLength());
        var interaction = Assert.Single(await InteractionsAsync(tenant.Id));
        Assert.Equal((AiFeature.PartFromPhotos, true, userId, "1 foto, código P-10"), (interaction.Feature, interaction.Success, interaction.UserId!.Value, interaction.InputSummary));
        Assert.True(interaction.InputTokens > 0);
        Assert.Contains("condicao", interaction.Output, StringComparison.Ordinal);
        Assert.Equal(1, await UsedAsync(tenant.Id));

        var plan = (await (await GetAsync(api.PanelClient(), owner, "/api/painel/plano")).Content.ReadFromJsonAsync<PlanDto>())!;
        Assert.Equal((1, 50), plan.Usage.Single(u => u.Key == PlanLimit.AiListingsPerMonth) is var u ? (u.Used, u.Limit!.Value) : default);
    }

    [Fact]
    public async Task Falha_e_resposta_invalida_sao_registradas_e_nao_contam()
    {
        var tenant = await api.CreateTenantAsync();

        var down = await Assert.ThrowsAsync<AiUnavailableException>(() => CallAsync(tenant.Id, Request(prompt: "P-11 " + FakeAiCompletion.FailMarker)));
        Assert.True(down.Transient);
        await Assert.ThrowsAsync<AiInvalidOutputException>(() => CallAsync(tenant.Id, Request(prompt: "P-12 " + FakeAiCompletion.InvalidMarker)));

        var interactions = await InteractionsAsync(tenant.Id);
        Assert.Equal(2, interactions.Count);
        Assert.All(interactions, i => Assert.False(i.Success));
        Assert.Equal("não é JSON", interactions[1].Output);
        Assert.True(interactions[1].InputTokens > 0);
        Assert.Equal(0, await UsedAsync(tenant.Id));
    }

    [Fact]
    public async Task Limite_do_plano_vale_sob_concorrencia()
    {
        var tenant = await api.CreateTenantAsync();
        var period = AiUsageCounter.PeriodOf(BillingCalendar.Today(TimeProvider.System));
        // Essencial: 50 por mês (Q41). Já usados 47: de 10 chamadas simultâneas, só 3 passam.
        await InTenantAsync(tenant.Id, db => db.Database.ExecuteSqlAsync(
            $"INSERT INTO ai_usage_counters (tenant_id, feature, period, used) VALUES ({tenant.Id}, {AiFeature.PartFromPhotos}, {period}, 47)"));

        var calls = Enumerable.Range(0, 10).Select(async _ =>
        {
            try
            {
                await CallAsync(tenant.Id, Request());
                return true;
            }
            catch (AiQuotaExceededException e)
            {
                Assert.Contains("O plano Essencial permite até 50 cadastros com IA por mês", e.Message, StringComparison.Ordinal);
                return false;
            }
        });
        var outcomes = await Task.WhenAll(calls);

        Assert.Equal(3, outcomes.Count(ok => ok));
        Assert.Equal(50, await UsedAsync(tenant.Id));
        // Recusado pelo limite não chega ao provedor: só as 3 chamadas ficam no registro.
        Assert.Equal(3, (await InteractionsAsync(tenant.Id)).Count);
    }

    [Fact]
    public async Task Outras_funcionalidades_sao_contadas_sem_limite()
    {
        var tenant = await api.CreateTenantAsync();
        await InTenantAsync(tenant.Id, db => db.Database.ExecuteSqlAsync(
            $"INSERT INTO ai_usage_counters (tenant_id, feature, period, used) VALUES ({tenant.Id}, {AiFeature.BuyerAnswer}, {AiUsageCounter.PeriodOf(BillingCalendar.Today(TimeProvider.System))}, 5000)"));

        await CallAsync(tenant.Id, Request(AiFeature.BuyerAnswer, "Pergunta: serve no Gol 2010?"));

        Assert.Equal(5001, await UsedAsync(tenant.Id, AiFeature.BuyerAnswer));
        await Assert.ThrowsAsync<ArgumentException>(() => CallAsync(tenant.Id, Request("inventada")));
    }

    [Fact]
    public async Task Cada_loja_ve_so_o_proprio_registro_e_contador()
    {
        var a = await api.CreateTenantAsync();
        var b = await api.CreateTenantAsync();

        await CallAsync(a.Id, Request());
        await CallAsync(a.Id, Request());
        await CallAsync(b.Id, Request());

        Assert.Equal(2, (await InteractionsAsync(a.Id)).Count);
        Assert.Single(await InteractionsAsync(b.Id));
        Assert.Equal((2, 1), (await UsedAsync(a.Id), await UsedAsync(b.Id)));
        Assert.All(await InteractionsAsync(b.Id), i => Assert.Equal(b.Id, i.TenantId));
    }

    [Fact]
    public async Task Sem_loja_no_escopo_nao_chama()
    {
        await using var scope = api.Services.CreateAsyncScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IAiCompletion>().CompleteAsync(Request()));
    }

    [Fact]
    public void Provedores_de_IA_nao_recebem_nada_que_altere_dados()
    {
        // RF35 CA2: quem fala com o modelo só tem opções, cliente HTTP e relógio — nada de banco, fila ou serviços de comando.
        var forbidden = new[] { typeof(TenantDbContext), typeof(Ecommerce.Infrastructure.Platform.PlatformDbContext), typeof(Wolverine.IMessageBus),
            typeof(IServiceProvider), typeof(IServiceScopeFactory) };
        var providers = typeof(AiOptions).Assembly.GetTypes()
            .Where(t => typeof(IAiCompletion).IsAssignableFrom(t) && !t.IsAbstract && t != typeof(AuditedAiCompletion) && t != typeof(QuotaAiCompletion))
            .ToList();
        Assert.Contains(typeof(AnthropicAiCompletion), providers);
        foreach (var provider in providers)
            foreach (var parameter in provider.GetConstructors().SelectMany(c => c.GetParameters()))
                Assert.DoesNotContain(parameter.ParameterType, forbidden);
    }
}
