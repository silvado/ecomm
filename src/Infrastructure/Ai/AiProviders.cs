using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Ecommerce.Application.Ai;
using Microsoft.Extensions.Options;

namespace Ecommerce.Infrastructure.Ai;

/// <summary>
/// IA da Anthropic pelo SDK oficial (pacote <c>Anthropic</c>, https://platform.claude.com/docs/en/cli-sdks-libraries/sdks/csharp).
/// Saída estruturada por <c>output_config.format</c> com JSON Schema — os modelos mais novos não aceitam forçar ferramenta
/// (https://platform.claude.com/docs/en/build-with-claude/structured-outputs). Nenhuma ferramenta é oferecida ao modelo:
/// ele só devolve texto (RF35 CA2). Erros: https://platform.claude.com/docs/en/api/errors.
/// </summary>
public sealed class AnthropicAiCompletion(AnthropicClient client, IOptions<AiOptions> options) : IAiCompletion
{
    public async Task<AiResult> CompleteAsync(AiRequest request, CancellationToken ct = default)
    {
        var model = options.Value.ModelFor(request.Feature);
        if (string.IsNullOrWhiteSpace(model)) throw new AiUnavailableException("IA sem modelo configurado para esta funcionalidade.", transient: false);

        var content = new List<ContentBlockParam>();
        foreach (var image in request.Images)
            content.Add(new ImageBlockParam(new Base64ImageSource { Data = Convert.ToBase64String(image.Data.Span), MediaType = MediaTypeOf(image.MediaType) }));
        content.Add(new TextBlockParam { Text = request.Prompt });

        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = request.MaxOutputTokens,
            System = request.Instructions,
            Messages = [new() { Role = Role.User, Content = content }],
            OutputConfig = new OutputConfig { Format = new JsonOutputFormat { Schema = SchemaOf(request.OutputSchema) } },
        };

        Message message;
        try
        {
            message = await client.Messages.Create(parameters, ct);
        }
        catch (AnthropicRateLimitException e)
        {
            throw new AiUnavailableException("A IA está com muitos pedidos agora. Tente de novo em instantes.", transient: true, e);
        }
        catch (Anthropic5xxException e)
        {
            throw new AiUnavailableException("A IA está fora do ar no momento. Tente de novo em instantes.", transient: true, e);
        }
        catch (AnthropicIOException e)
        {
            throw new AiUnavailableException("Não foi possível falar com a IA. Tente de novo em instantes.", transient: true, e);
        }
        catch (AnthropicApiException e)
        {
            // 4xx: configuração (chave, modelo) ou pedido inválido — não adianta repetir. A mensagem do provedor não vai ao usuário.
            throw new AiUnavailableException("A IA recusou o pedido. A equipe da plataforma foi avisada.", transient: false, e);
        }

        var used = message.Model.Raw();
        var input = (int)message.Usage.InputTokens;
        var output = (int)message.Usage.OutputTokens;
        var text = string.Concat(message.Content.Select(b => b.TryPickText(out var t) ? t.Text : string.Empty));

        if (message.StopReason is { } stop && stop.Raw() is "refusal" or "max_tokens")
            throw new AiInvalidOutputException(stop.Raw() == "refusal" ? "A IA recusou responder." : "A resposta da IA veio cortada.", used, input, output, text);
        return new AiResult(ParseObject(text, used, input, output), used, input, output);
    }

    internal static JsonElement ParseObject(string text, string model, int input, int output)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Cai no erro abaixo.
        }
        throw new AiInvalidOutputException("A resposta da IA não veio no formato esperado.", model, input, output, text);
    }

    private static Dictionary<string, JsonElement> SchemaOf(JsonObject schema) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(schema.ToJsonString())!;

    private static MediaType MediaTypeOf(string mediaType) => mediaType switch
    {
        "image/jpeg" => MediaType.ImageJpeg,
        "image/png" => MediaType.ImagePng,
        "image/gif" => MediaType.ImageGif,
        "image/webp" => MediaType.ImageWebP,
        _ => throw new ArgumentException("Tipo de imagem não aceito pela IA (use JPEG, PNG, GIF ou WebP).", nameof(mediaType)),
    };
}

/// <summary>
/// IA simulada (dev e testes; a API recusa subir com ela em produção). Devolve um objeto do formato pedido, com valores
/// vazios. Para testar falhas: <c>[falhar]</c> no pedido = provedor fora do ar; <c>[invalida]</c> = resposta fora do formato.
/// </summary>
public sealed class FakeAiCompletion : IAiCompletion
{
    public const string Model = "simulado";
    public const string FailMarker = "[falhar]";
    public const string InvalidMarker = "[invalida]";

    public Task<AiResult> CompleteAsync(AiRequest request, CancellationToken ct = default)
    {
        if (request.Prompt.Contains(FailMarker, StringComparison.Ordinal))
            throw new AiUnavailableException("A IA simulada está fora do ar.", transient: true);
        var tokens = (request.Instructions.Length + request.Prompt.Length) / 4 + request.Images.Count * 1_000;
        if (request.Prompt.Contains(InvalidMarker, StringComparison.Ordinal))
            throw new AiInvalidOutputException("A resposta da IA não veio no formato esperado.", Model, tokens, 10, "não é JSON");
        var output = JsonSerializer.SerializeToElement(Sample(request.OutputSchema));
        return Task.FromResult(new AiResult(output, Model, tokens, output.GetRawText().Length / 4));
    }

    /// <summary>Objeto com cada campo no valor vazio do seu tipo (o primeiro valor do enum, se houver).</summary>
    internal static JsonNode? Sample(JsonNode? schema)
    {
        if (schema is not JsonObject s) return null;
        if (s["enum"] is JsonArray values && values.Count > 0) return values[0]?.DeepClone();
        return (s["type"] as JsonValue)?.GetValue<string>() switch
        {
            "object" => new JsonObject((s["properties"] as JsonObject ?? []).Select(p => KeyValuePair.Create(p.Key, Sample(p.Value)))),
            "array" => new JsonArray(),
            "string" => JsonValue.Create(string.Empty),
            "integer" or "number" => JsonValue.Create(0),
            "boolean" => JsonValue.Create(false),
            _ => null,
        };
    }
}

/// <summary>IA desligada (sem provedor configurado): toda chamada falha com mensagem clara, sem custo.</summary>
public sealed class UnconfiguredAiCompletion : IAiCompletion
{
    public Task<AiResult> CompleteAsync(AiRequest request, CancellationToken ct = default) =>
        throw new AiUnavailableException("A IA ainda não está disponível nesta instalação.", transient: false);
}
