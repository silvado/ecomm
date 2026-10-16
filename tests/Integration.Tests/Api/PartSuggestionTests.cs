using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ecommerce.Application.Ai;
using Ecommerce.Application.Catalog;
using Ecommerce.Domain.Ai;
using Ecommerce.Domain.Catalog;
using Ecommerce.Infrastructure.Billing;
using Ecommerce.Infrastructure.Catalog;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Ecommerce.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF30 — cadastro assistido por foto: a sugestão preenche o formulário sem gravar nada, conta 1 no plano só quando dá certo,
/// fotos vão reduzidas e sem metadados, e só veículos que existem na tabela viram sugestão de compatibilidade.
/// </summary>
public sealed class PartSuggestionTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private const string Vehicles = """
        marca;modelo;motorizacao;ano_inicial;ano_final
        Volkswagen;Gol;1.0 8V Flex (G5);2009;2012
        Volkswagen;Gol;1.6 8V Flex (G5);2009;2012
        Volkswagen;Voyage;1.6 8V Flex;2009;2012
        Volkswagen;Fox;1.0 8V Flex;2004;2009
        Fiat;Strada;1.3 Firefly Flex;2021;
        """;

    private sealed record CompatibilityDto(Guid VehicleVersionId, string Brand, string Model, string Engine, int VersionYearFrom, int? VersionYearTo, int? YearFrom, int? YearTo);
    private sealed record SuggestionDto(string Title, string Description, string Condition, List<string> OemCodes, List<CompatibilityDto> Compatibilities, List<string> UnmatchedVehicles);
    private sealed record PartCompatibilityDto(Guid VehicleVersionId, bool SuggestedByAi);
    private sealed record PartDto(Guid Id, List<PartCompatibilityDto> Compatibilities);

    /// <summary>IA de teste: devolve a resposta dada e guarda o pedido.</summary>
    private sealed class StubAi(JsonObject output) : IAiCompletion
    {
        public AiRequest? Request { get; private set; }

        public Task<AiResult> CompleteAsync(AiRequest request, CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult(new AiResult(JsonSerializer.SerializeToElement(output), "stub", 1, 1));
        }
    }

    private static async Task<HttpResponseMessage> SuggestAsync(HttpClient client, string token, IEnumerable<byte[]> photos, string? notes = null)
    {
        using var form = new MultipartFormDataContent();
        var index = 0;
        foreach (var photo in photos)
        {
            var content = new ByteArrayContent(photo);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(content, "fotos", $"foto{++index}.jpg");
        }
        if (notes is not null) form.Add(new StringContent(notes), "informacoes");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/painel/pecas/sugestao-ia") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private async Task<T> InTenantAsync<T>(Guid tenantId, Func<TenantDbContext, Task<T>> read)
    {
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        return await read(scope.ServiceProvider.GetRequiredService<TenantDbContext>());
    }

    private Task<int> UsedAsync(Guid tenantId) => InTenantAsync(tenantId, db =>
        db.AiUsageCounters.Where(c => c.Feature == AiFeature.PartFromPhotos).Select(c => c.Used).SingleOrDefaultAsync());

    /// <summary>Roda o serviço com a IA de teste, direto (sem limite nem registro), na loja informada.</summary>
    private async Task<(PartSuggestionResult Result, StubAi Ai)> WithStubAsync(Guid tenantId, JsonObject output, params byte[][] photos)
    {
        var stub = new StubAi(output);
        var result = await InTenantAsync(tenantId, db =>
            new PartSuggestionService(stub, db, TimeProvider.System).SuggestAsync(photos.Select(p => new SuggestionPhoto(p)).ToList(), "nada"));
        return (result, stub);
    }

    private static JsonObject Output(params JsonObject[] vehicles) => new()
    {
        ["titulo"] = new string('T', 200),
        ["descricao"] = "Peça usada.",
        ["condicao"] = "recondicionada",
        ["codigos_oem"] = new JsonArray("5U0 941 015", "5u0-941-015", new string('9', 40), ""),
        ["veiculos"] = new JsonArray(vehicles.Select(v => (JsonNode)v).ToArray()),
    };

    private static JsonObject Vehicle(string brand, string model, string engine = "", int from = 0, int to = 0) =>
        new() { ["marca"] = brand, ["modelo"] = model, ["motorizacao"] = engine, ["ano_inicial"] = from, ["ano_final"] = to };

    [Fact]
    public async Task Sugestao_preenche_sem_gravar_e_conta_uma_unidade_do_plano()
    {
        Assert.True((await api.ImportVehiclesAsync(Vehicles)).Succeeded);
        var tenant = await api.CreateTenantAsync();
        var operador = await api.SessionForAsync(tenant, Ecommerce.Domain.Identity.TenantRole.Operator);

        var response = await SuggestAsync(api.PanelClient(), operador, [TestImages.Jpeg(800, 600), TestImages.Png(300, 300)], "retirado de Gol 2011");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var suggestion = (await response.Content.ReadFromJsonAsync<SuggestionDto>())!;
        Assert.Equal(("Farol dianteiro esquerdo Gol G5", "used", "5U0 941 015"), (suggestion.Title, suggestion.Condition, suggestion.OemCodes.Single()));
        // "Gol G5" casa com o modelo Gol e a geração G5 da motorização; "Voyage 1.6" com a versão 1.6; Tempra não existe na tabela.
        Assert.Equal([("Gol", "1.0 8V Flex (G5)"), ("Gol", "1.6 8V Flex (G5)"), ("Voyage", "1.6 8V Flex")],
            suggestion.Compatibilities.Select(c => (c.Model, c.Engine)));
        Assert.All(suggestion.Compatibilities, c => Assert.Equal(((int?)null, (int?)null), (c.YearFrom, c.YearTo)));
        Assert.Equal("Fiat Tempra 2.0 1995–1998", Assert.Single(suggestion.UnmatchedVehicles));

        Assert.Equal(1, await UsedAsync(tenant.Id));
        var interaction = Assert.Single(await InTenantAsync(tenant.Id, db => db.AiInteractions.AsNoTracking().ToListAsync()));
        Assert.Equal((AiFeature.PartFromPhotos, true, "2 foto(s); informações: retirado de Gol 2011"), (interaction.Feature, interaction.Success, interaction.InputSummary));
        Assert.Equal(0, await InTenantAsync(tenant.Id, db => db.Parts.CountAsync()));
    }

    [Fact]
    public async Task Fotos_invalidas_sao_recusadas_sem_chamar_a_IA()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var client = api.PanelClient();

        Assert.Equal("Envie ao menos uma foto da peça.", await TitleAsync(await SuggestAsync(client, owner, [], "só texto")));
        Assert.Equal("Envie no máximo 10 fotos.", await TitleAsync(await SuggestAsync(client, owner, Enumerable.Repeat(TestImages.Jpeg(10, 10), 11))));
        Assert.Equal("Foto 2: formato não aceito. Envie JPG, PNG ou WebP.",
            await TitleAsync(await SuggestAsync(client, owner, [TestImages.Jpeg(10, 10), "não é imagem"u8.ToArray()])));
        Assert.StartsWith("Foto 1: Não foi possível ler a imagem", await TitleAsync(await SuggestAsync(client, owner, [TestImages.CorruptJpeg()])), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await SuggestAsync(client, owner, [TestImages.Jpeg(10, 10)], new string('x', 501))).StatusCode);

        Assert.Equal(0, await UsedAsync(tenant.Id));
        Assert.Equal(0, await InTenantAsync(tenant.Id, db => db.AiInteractions.CountAsync()));
    }

    [Fact]
    public async Task Limite_do_plano_e_falhas_da_IA_viram_mensagem_e_nao_contam()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var client = api.PanelClient();

        var down = await SuggestAsync(client, owner, [TestImages.Jpeg(10, 10)], Ecommerce.Infrastructure.Ai.FakeAiCompletion.FailMarker);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        var invalid = await SuggestAsync(client, owner, [TestImages.Jpeg(10, 10)], Ecommerce.Infrastructure.Ai.FakeAiCompletion.InvalidMarker);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.StartsWith("A IA não conseguiu sugerir", await TitleAsync(invalid), StringComparison.Ordinal);
        Assert.Equal(0, await UsedAsync(tenant.Id));

        // Essencial: 50 por mês (Q41).
        await InTenantAsync(tenant.Id, db => db.Database.ExecuteSqlAsync(
            $"INSERT INTO ai_usage_counters (tenant_id, feature, period, used) VALUES ({tenant.Id}, {AiFeature.PartFromPhotos}, {AiUsageCounter.PeriodOf(BillingCalendar.Today(TimeProvider.System))}, 50) ON CONFLICT (tenant_id, feature, period) DO UPDATE SET used = 50"));
        var quota = await SuggestAsync(client, owner, [TestImages.Jpeg(10, 10)]);
        Assert.Equal(HttpStatusCode.Conflict, quota.StatusCode);
        Assert.Contains("O plano Essencial permite até 50 cadastros com IA por mês", await TitleAsync(quota), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fotos_vao_para_a_IA_reduzidas_em_JPEG_e_sem_metadados()
    {
        var tenant = await api.CreateTenantAsync();

        var (result, ai) = await WithStubAsync(tenant.Id, Output(), TestImages.Jpeg(3000, 1500, orientation: 6), TestImages.Png(200, 100));

        Assert.Equal(SuggestionOutcome.Done, result.Outcome);
        Assert.Equal(AiFeature.PartFromPhotos, ai.Request!.Feature);
        Assert.All(ai.Request.Images, i => Assert.Equal("image/jpeg", i.MediaType));
        var (width, height, fields) = TestImages.Inspect(ai.Request.Images[0].Data.ToArray());
        // Rotação EXIF aplicada (retrato) e lado maior no limite.
        Assert.Equal((PhotoProcessor.AiMaxEdge / 2, PhotoProcessor.AiMaxEdge), (width, height));
        Assert.DoesNotContain("exif-data", fields);
        Assert.DoesNotContain("orientation", fields);
        var (pngWidth, pngHeight, _) = TestImages.Inspect(ai.Request.Images[1].Data.ToArray());
        Assert.Equal((200, 100), (pngWidth, pngHeight));
        // A IA não recebe nada de preço para sugerir (RF35).
        Assert.DoesNotContain("preco", ai.Request.OutputSchema.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resposta_da_IA_e_saneada_antes_de_chegar_ao_formulario()
    {
        var tenant = await api.CreateTenantAsync();

        var (result, _) = await WithStubAsync(tenant.Id, Output(), TestImages.Jpeg(10, 10));

        var suggestion = result.Suggestion!;
        Assert.Equal(Part.TitleMaxLength, suggestion.Title.Length);
        Assert.Equal(PartCondition.Refurbished, suggestion.Condition);
        // Mesmo código escrito de dois jeitos vira um só; longo demais ou vazio não é código.
        Assert.Equal(["5U0 941 015"], suggestion.OemCodes);
    }

    [Fact]
    public async Task So_versoes_que_existem_na_tabela_viram_compatibilidade()
    {
        Assert.True((await api.ImportVehiclesAsync(Vehicles)).Succeeded);
        var tenant = await api.CreateTenantAsync();

        var (result, _) = await WithStubAsync(tenant.Id, Output(
                Vehicle("VOLKSWAGEN", "gol", "1.6", 2011, 2012), // só a 1.6, anos restritos
                Vehicle("Volkswagen", "Gol G6"), // geração que não está na tabela
                Vehicle("Volkswagen", "Fox", "", 2015, 2018), // fora da faixa da versão
                Vehicle("Fiat", "Strada", "1.3", 2023, 0), // versão em produção: até o ano atual
                Vehicle("Citroën", "C3"),
                Vehicle("Volkswagen", "Gol", "1.6 8V", 2011, 2012)), // repetida: não duplica
            TestImages.Jpeg(10, 10));

        var suggestion = result.Suggestion!;
        Assert.Equal([("Gol", "1.6 8V Flex (G5)", (int?)2011, (int?)2012), ("Strada", "1.3 Firefly Flex", 2023, DateTimeOffset.UtcNow.Year)],
            suggestion.Compatibilities.Select(c => (c.Model, c.Engine, c.YearFrom, c.YearTo)));
        Assert.Equal(["Volkswagen Gol G6", "Volkswagen Fox 2015–2018", "Citroën C3"], suggestion.UnmatchedVehicles);
    }

    [Fact]
    public async Task Compatibilidade_mantida_da_sugestao_fica_marcada_como_da_IA()
    {
        Assert.True((await api.ImportVehiclesAsync(Vehicles)).Succeeded);
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var client = api.PanelClient();
        var suggestion = (await (await SuggestAsync(client, owner, [TestImages.Jpeg(10, 10)])).Content.ReadFromJsonAsync<SuggestionDto>())!;
        var created = await SendAsync(client, owner, HttpMethod.Post, "/api/painel/pecas", new
        {
            internalCode = "IA-1", title = suggestion.Title, description = suggestion.Description, condition = suggestion.Condition, price = 250m,
            quantity = 1, oemCodes = suggestion.OemCodes,
        });
        var part = (await created.Content.ReadFromJsonAsync<PartDto>())!;

        var first = suggestion.Compatibilities[0];
        await SendAsync(client, owner, HttpMethod.Post, $"/api/painel/pecas/{part.Id}/compatibilidades",
            new { first.VehicleVersionId, first.YearFrom, first.YearTo, suggestedByAi = true });
        var view = (await (await SendAsync(client, owner, HttpMethod.Post, $"/api/painel/pecas/{part.Id}/compatibilidades",
            new { suggestion.Compatibilities[1].VehicleVersionId })).Content.ReadFromJsonAsync<PartDto>())!;

        Assert.True(view.Compatibilities.Single(c => c.VehicleVersionId == first.VehicleVersionId).SuggestedByAi);
        Assert.False(view.Compatibilities.Single(c => c.VehicleVersionId == suggestion.Compatibilities[1].VehicleVersionId).SuggestedByAi);
        Assert.Equal(CompatibilitySource.AiSuggested, await InTenantAsync(tenant.Id, db =>
            db.Set<PartCompatibility>().Where(c => c.VehicleVersionId == first.VehicleVersionId).Select(c => c.Source).SingleAsync()));
    }
}
