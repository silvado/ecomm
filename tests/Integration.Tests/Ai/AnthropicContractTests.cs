using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Core;
using Ecommerce.Application.Ai;
using Ecommerce.Domain.Ai;
using Ecommerce.Infrastructure.Ai;
using Microsoft.Extensions.Options;

namespace Ecommerce.Integration.Tests.Ai;

/// <summary>
/// Contrato do adaptador da Anthropic contra a Messages API simulada (regra 6/8; formato em
/// https://platform.claude.com/docs/en/api/messages e https://platform.claude.com/docs/en/build-with-claude/structured-outputs).
/// </summary>
public sealed class AnthropicContractTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["titulo"] = new JsonObject { ["type"] = "string" } },
        ["required"] = new JsonArray("titulo"),
        ["additionalProperties"] = false,
    };

    private static string Message(string text, string stopReason = "end_turn") => JsonSerializer.Serialize(new
    {
        id = "msg_01",
        type = "message",
        role = "assistant",
        model = "modelo-da-resposta",
        content = new[] { new { type = "text", text } },
        stop_reason = stopReason,
        stop_sequence = (string?)null,
        usage = new { input_tokens = 1200, output_tokens = 35 },
    });

    private static (AnthropicAiCompletion Adapter, StubHandler Handler) Adapter(HttpStatusCode status, string body)
    {
        var handler = new StubHandler(status, body);
        var client = new AnthropicClient(new ClientOptions
        {
            HttpClient = new HttpClient(handler),
            BaseUrl = "https://api.anthropic.test",
            ApiKey = "chave-de-teste",
            MaxRetries = 0,
        });
        var options = Options.Create(new AiOptions
        {
            DefaultModel = "modelo-padrao",
            Models = { [AiFeature.PartFromPhotos] = "modelo-com-visao" },
        });
        return (new AnthropicAiCompletion(client, options), handler);
    }

    private static AiRequest Request() => new(AiFeature.PartFromPhotos, "Você cadastra autopeças usadas.", "Código P-10.",
        [new AiImage("image/jpeg", new byte[] { 1, 2, 3 })], Schema, "1 foto");

    [Fact]
    public async Task Envia_modelo_da_funcionalidade_imagem_e_formato_JSON_sem_ferramentas()
    {
        var (adapter, handler) = Adapter(HttpStatusCode.OK, Message("""{"titulo":"Farol esquerdo Gol G5"}"""));

        var result = await adapter.CompleteAsync(Request());

        Assert.Equal("Farol esquerdo Gol G5", result.Output.GetProperty("titulo").GetString());
        Assert.Equal(("modelo-da-resposta", 1200, 35), (result.Model, result.InputTokens, result.OutputTokens));
        Assert.Equal("https://api.anthropic.test/v1/messages", handler.Request!.RequestUri!.ToString());
        Assert.Equal("chave-de-teste", handler.Request.Headers.GetValues("x-api-key").Single());
        Assert.True(handler.Request.Headers.Contains("anthropic-version"));

        var body = JsonNode.Parse(handler.RequestBody!)!;
        Assert.Equal("modelo-com-visao", (string?)body["model"]);
        Assert.Equal("Você cadastra autopeças usadas.", (string?)body["system"]);
        Assert.Equal("json_schema", (string?)body["output_config"]!["format"]!["type"]);
        Assert.Equal("object", (string?)body["output_config"]!["format"]!["schema"]!["type"]);
        var content = body["messages"]![0]!["content"]!.AsArray();
        Assert.Equal(("image", "base64", "image/jpeg", "AQID"),
            ((string?)content[0]!["type"], (string?)content[0]!["source"]!["type"], (string?)content[0]!["source"]!["media_type"], (string?)content[0]!["source"]!["data"]));
        Assert.Equal("text", (string?)content[1]!["type"]);
        // RF35 CA2: o modelo não recebe nenhuma ferramenta — só responde.
        Assert.Null(body["tools"]);
    }

    [Theory]
    [InlineData("refusal", "recusou")]
    [InlineData("max_tokens", "cortada")]
    public async Task Recusa_ou_resposta_cortada_e_descartada(string stopReason, string message)
    {
        var (adapter, _) = Adapter(HttpStatusCode.OK, Message("""{"titulo":""", stopReason));

        var error = await Assert.ThrowsAsync<AiInvalidOutputException>(() => adapter.CompleteAsync(Request()));

        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.Equal((1200, 35), (error.InputTokens, error.OutputTokens));
    }

    [Fact]
    public async Task Texto_fora_do_formato_e_descartado()
    {
        var (adapter, _) = Adapter(HttpStatusCode.OK, Message("Desculpe, não consigo."));

        await Assert.ThrowsAsync<AiInvalidOutputException>(() => adapter.CompleteAsync(Request()));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limit_error", true)]
    [InlineData((HttpStatusCode)529, "overloaded_error", true)]
    [InlineData(HttpStatusCode.BadRequest, "invalid_request_error", false)]
    [InlineData(HttpStatusCode.Unauthorized, "authentication_error", false)]
    public async Task Erros_da_API_viram_indisponivel_com_ou_sem_nova_tentativa(HttpStatusCode status, string type, bool transient)
    {
        var (adapter, _) = Adapter(status, JsonSerializer.Serialize(new { type = "error", error = new { type, message = "segredo-do-provedor" } }));

        var error = await Assert.ThrowsAsync<AiUnavailableException>(() => adapter.CompleteAsync(Request()));

        Assert.Equal(transient, error.Transient);
        Assert.DoesNotContain("segredo-do-provedor", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Imagem_de_tipo_nao_aceito_nem_chega_a_API()
    {
        var (adapter, handler) = Adapter(HttpStatusCode.OK, Message("{}"));

        await Assert.ThrowsAsync<ArgumentException>(() => adapter.CompleteAsync(Request() with { Images = [new AiImage("image/bmp", new byte[] { 1 })] }));
        Assert.Null(handler.Request);
    }

    [Fact]
    public void Custo_estimado_pela_tabela_de_precos()
    {
        var options = new AiOptions { Prices = { ["modelo"] = new AiModelPrice { InputPerMillion = 3m, OutputPerMillion = 15m } } };

        Assert.Equal(0.0036m + 0.000525m, options.CostOf("modelo", 1200, 35));
        Assert.Equal(0m, options.CostOf("sem-preco", 1200, 35));
    }
}
