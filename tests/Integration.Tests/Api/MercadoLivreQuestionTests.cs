using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Ecommerce.Domain.Channels;
using Ecommerce.Infrastructure.Channels.MercadoLivre;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Ecommerce.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF31 — perguntas de compradores do Mercado Livre (ML e IA simulados, webhook real): rascunho da IA para a loja revisar,
/// resposta automática só das simples e só com a opção ligada (desligada por padrão), compatibilidade sempre como rascunho
/// (CA3), resposta enviada ao ML uma vez, pergunta respondida ou excluída no ML acompanhada, e isolamento entre lojas.
/// </summary>
public sealed class MercadoLivreQuestionTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private const string Webhook = "/api/webhooks/canais/" + SalesChannel.MercadoLivre;
    private static readonly string[] Oem = ["5U0941015"];

    private sealed record StartDto(string AuthorizationUrl);
    private sealed record IdDto(Guid Id);
    private sealed record ListingDto(string Channel, string? ExternalId);
    private sealed record PublishedDto(List<ListingDto> Listings);
    private sealed record QuestionDto(Guid Id, string Text, string Status, string? DraftText, string? Kind, string? DraftNote, string? AnswerText, string? AnsweredVia,
        string? PartTitle);
    private sealed record SettingsDto(bool AutoAnswer);

    private static string NewId() => Random.Shared.NextInt64(100_000_000, 999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private sealed record Shop(Guid TenantId, string Owner, string Seller);

    private async Task<Shop> ShopAsync()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var seller = NewId();
        var start = (await (await SendAsync(api.PanelClient(), owner, HttpMethod.Post, "/api/painel/canais/mercado-livre/conectar")).Content.ReadFromJsonAsync<StartDto>())!;
        var state = Uri.UnescapeDataString(start.AuthorizationUrl.Split("state=")[1].Split('&')[0]);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(api.PanelClient(), owner, HttpMethod.Post, "/api/painel/canais/mercado-livre/retorno",
            new { code = FakeMercadoLivreAuth.CodePrefix + seller, state })).StatusCode);
        return new Shop(tenant.Id, owner, seller);
    }

    private async Task<string> ListedItemAsync(Shop shop)
    {
        var created = await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Post, "/api/painel/pecas", new
        {
            internalCode = "PG-" + NewId()[..6], title = "Farol dianteiro esquerdo Gol", condition = "used", price = 350m, quantity = 1, oemCodes = Oem,
        });
        var partId = (await created.Content.ReadFromJsonAsync<IdDto>())!.Id;
        using var photo = new HttpRequestMessage(HttpMethod.Post, $"/api/painel/pecas/{partId}/fotos") { Content = new ByteArrayContent(TestImages.Jpeg(800, 600)) };
        photo.Headers.Authorization = new AuthenticationHeaderValue("Bearer", shop.Owner);
        Assert.Equal(HttpStatusCode.Created, (await api.PanelClient().SendAsync(photo)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Put, $"/api/painel/pecas/{partId}/situacao", new { status = "active" })).StatusCode);
        var published = await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Post, $"/api/painel/pecas/{partId}/mercado-livre/anunciar", new
        {
            categoryId = FakeMercadoLivreApi.HeadlightCategory, listingTypeId = "gold_special",
            attributes = new[]
            {
                new { id = "BRAND", valueId = (string?)null, valueName = (string?)"VW" },
                new { id = "ITEM_CONDITION", valueId = (string?)"2230581", valueName = (string?)null },
                new { id = "PART_NUMBER", valueId = (string?)null, valueName = (string?)"5U0941015" },
            },
        });
        Assert.Equal(HttpStatusCode.Created, published.StatusCode);
        return (await published.Content.ReadFromJsonAsync<PublishedDto>())!.Listings.Single(l => l.Channel == SalesChannel.MercadoLivre).ExternalId!;
    }

    private Task<HttpResponseMessage> NotifyAsync(string seller, string questionId, string received) =>
        api.ClientFor("api.plataforma.test", $"198.51.110.{Random.Shared.Next(1, 250)}").PostAsync(Webhook, new StringContent($$"""
            {"resource":"/questions/{{questionId}}","user_id":{{seller}},"topic":"questions","application_id":5503910054141466,"attempts":1,
             "sent":"{{received}}","received":"{{received}}"}
            """, Encoding.UTF8, "application/json"));

    private async Task<T> InTenantAsync<T>(Guid tenantId, Func<TenantDbContext, Task<T>> query)
    {
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        return await query(scope.ServiceProvider.GetRequiredService<TenantDbContext>());
    }

    private async Task<BuyerQuestion> QuestionAsync(Guid tenantId, string questionId, Func<BuyerQuestion, bool>? done = null)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var question = await InTenantAsync(tenantId, db => db.BuyerQuestions.AsNoTracking().SingleOrDefaultAsync(q => q.ExternalId == questionId));
            if (question is not null && (done?.Invoke(question) ?? question.DraftedAt is not null)) return question;
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) Assert.Fail($"Pergunta {questionId} {(question is null ? "não importada" : "em " + question.Status)}.");
            await Task.Delay(100);
        }
    }

    private async Task<string> AskAsync(Shop shop, string itemId, string text)
    {
        var questionId = FakeMercadoLivreApi.Ask(itemId, shop.Seller, text);
        Assert.Equal(HttpStatusCode.OK, (await NotifyAsync(shop.Seller, questionId, "2026-10-18T12:00:00Z")).StatusCode);
        return questionId;
    }

    private Task<HttpResponseMessage> AnswerAsync(Shop shop, Guid id, string text) =>
        SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Post, $"/api/painel/perguntas/{id}/responder", new { texto = text });

    [Fact]
    public async Task Pergunta_vira_rascunho_da_IA_e_a_loja_envia_a_resposta_revisada()
    {
        var shop = await ShopAsync();
        var itemId = await ListedItemAsync(shop);
        var questionId = await AskAsync(shop, itemId, "Ainda tem disponível?");

        // Resposta automática desligada por padrão (CA4): simples, mas espera a loja.
        var question = await QuestionAsync(shop.TenantId, questionId);
        Assert.Equal((BuyerQuestionStatus.Pending, QuestionKind.Simple), (question.Status, question.Kind));
        Assert.StartsWith("Olá! A peça está disponível", question.DraftText, StringComparison.Ordinal);
        Assert.Null(FakeMercadoLivreApi.Question(questionId)!["answer"]);

        var list = (await (await GetAsync(api.PanelClient(), shop.Owner, "/api/painel/perguntas")).Content.ReadFromJsonAsync<List<QuestionDto>>())!;
        var view = Assert.Single(list);
        Assert.Equal(("Farol dianteiro esquerdo Gol", "simple"), (view.PartTitle, view.Kind));

        var sent = await AnswerAsync(shop, view.Id, "  Temos sim! Pronta para envio.  ");
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var after = (await sent.Content.ReadFromJsonAsync<QuestionDto>())!;
        Assert.Equal(("answered", "panel"), (after.Status, after.AnsweredVia));
        Assert.Equal("Temos sim! Pronta para envio.", (string?)FakeMercadoLivreApi.Question(questionId)!["answer"]!["text"]);

        // Segundo envio (outra aba, clique duplo) não manda de novo; o aviso de "respondida" do ML não muda nada.
        Assert.Equal(HttpStatusCode.Conflict, (await AnswerAsync(shop, view.Id, "De novo")).StatusCode);
        await NotifyAsync(shop.Seller, questionId, "2026-10-18T12:05:00Z");
        Assert.Empty((await (await GetAsync(api.PanelClient(), shop.Owner, "/api/painel/perguntas")).Content.ReadFromJsonAsync<List<QuestionDto>>())!);
        Assert.Equal(1, await InTenantAsync(shop.TenantId, db => db.BuyerQuestions.CountAsync()));
    }

    [Fact]
    public async Task Com_a_automatica_ligada_a_simples_sai_sozinha_e_a_de_compatibilidade_vira_rascunho()
    {
        var shop = await ShopAsync();
        var itemId = await ListedItemAsync(shop);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Put, "/api/painel/perguntas/configuracao", new { automatica = true })).StatusCode);

        var simple = await AskAsync(shop, itemId, "Ainda tem disponível?");
        var answered = await QuestionAsync(shop.TenantId, simple, q => q.Status == BuyerQuestionStatus.Answered);
        Assert.Equal(AnswerOrigin.Automatic, answered.AnsweredVia);
        Assert.StartsWith("Olá! A peça está disponível", (string?)FakeMercadoLivreApi.Question(simple)!["answer"]!["text"], StringComparison.Ordinal);

        // A IA simulada erra de propósito ("Serve sim!", simples): a regra fixa segura (CA3) — a peça não tem veículos.
        var compatibility = await AskAsync(shop, itemId, "Serve no Gol 2010?");
        var draft = await QuestionAsync(shop.TenantId, compatibility);
        await Task.Delay(1000); // dá tempo de um eventual envio
        Assert.Equal((BuyerQuestionStatus.Pending, QuestionKind.Complex), (draft.Status, draft.Kind));
        Assert.Contains("não tem veículos cadastrados", draft.DraftNote, StringComparison.Ordinal);
        Assert.Null(FakeMercadoLivreApi.Question(compatibility)!["answer"]);
    }

    [Fact]
    public async Task Respondida_ou_excluida_no_ML_sai_dos_pendentes()
    {
        var shop = await ShopAsync();
        var itemId = await ListedItemAsync(shop);
        var answeredThere = await AskAsync(shop, itemId, "Tem a lanterna também?");
        var deleted = await AskAsync(shop, itemId, "Está inteiro?");
        var first = await QuestionAsync(shop.TenantId, answeredThere);
        await QuestionAsync(shop.TenantId, deleted);

        FakeMercadoLivreApi.ChangeQuestion(answeredThere, "ANSWERED", "Não temos, só o farol.");
        FakeMercadoLivreApi.ChangeQuestion(deleted, "DELETED");
        await NotifyAsync(shop.Seller, answeredThere, "2026-10-18T13:00:00Z");
        await NotifyAsync(shop.Seller, deleted, "2026-10-18T13:00:00Z");

        var channel = await QuestionAsync(shop.TenantId, answeredThere, q => q.Status == BuyerQuestionStatus.Answered);
        Assert.Equal((AnswerOrigin.Channel, "Não temos, só o farol."), (channel.AnsweredVia!.Value, channel.AnswerText));
        await QuestionAsync(shop.TenantId, deleted, q => q.Status == BuyerQuestionStatus.Closed);
        Assert.Equal(HttpStatusCode.Conflict, (await AnswerAsync(shop, first.Id, "Oi")).StatusCode);
    }

    [Fact]
    public async Task IA_fora_do_ar_deixa_a_nota_e_a_loja_responde_a_mao()
    {
        var shop = await ShopAsync();
        var itemId = await ListedItemAsync(shop);
        var questionId = await AskAsync(shop, itemId, "Qual o estado? " + Ecommerce.Infrastructure.Ai.FakeAiCompletion.FailMarker);

        var question = await QuestionAsync(shop.TenantId, questionId);
        Assert.Null(question.DraftText);
        Assert.Contains("Responda à mão", question.DraftNote, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await AnswerAsync(shop, question.Id, "Bom estado, sem trincas.")).StatusCode);
    }

    [Fact]
    public async Task Pergunta_de_anuncio_que_nao_e_da_plataforma_fica_sem_rascunho()
    {
        var shop = await ShopAsync();
        var questionId = await AskAsync(shop, "MLB" + NewId(), "Tem garantia?");

        var question = await QuestionAsync(shop.TenantId, questionId);
        Assert.Null(question.PartId);
        Assert.Null(question.DraftText);
        Assert.StartsWith("Anúncio que não foi publicado pela plataforma", question.DraftNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cada_loja_ve_e_responde_so_as_proprias_perguntas()
    {
        var shop = await ShopAsync();
        var other = await ShopAsync();
        var itemId = await ListedItemAsync(shop);
        var question = await QuestionAsync(shop.TenantId, await AskAsync(shop, itemId, "Ainda tem?"));

        Assert.Empty((await (await GetAsync(api.PanelClient(), other.Owner, "/api/painel/perguntas?situacao=todas")).Content.ReadFromJsonAsync<List<QuestionDto>>())!);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(api.PanelClient(), other.Owner, HttpMethod.Post, $"/api/painel/perguntas/{question.Id}/responder",
            new { texto = "Invasão" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(api.PanelClient(), other.Owner, HttpMethod.Post, $"/api/painel/perguntas/{question.Id}/rascunho")).StatusCode);
        Assert.False(await InTenantAsync(other.TenantId, db => db.BuyerQuestions.IgnoreQueryFilters().AnyAsync()));
        Assert.False((await (await GetAsync(api.PanelClient(), other.Owner, "/api/painel/perguntas/configuracao")).Content.ReadFromJsonAsync<SettingsDto>())!.AutoAnswer);
        Assert.Equal(BuyerQuestionStatus.Pending, (await QuestionAsync(shop.TenantId, question.ExternalId)).Status);
    }
}
