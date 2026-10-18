using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ecommerce.Application.Channels;
using Microsoft.Extensions.Options;

namespace Ecommerce.Infrastructure.Channels.Meta;

public sealed class MetaOptions
{
    public const string Section = "Meta";

    /// <summary>Dev e testes: Graph API simulada (a API recusa subir assim em produção).</summary>
    public bool UseFake { get; set; }

    public string GraphUrl { get; set; } = "https://graph.facebook.com";

    /// <summary>Versão da Graph API (a da documentação consultada).</summary>
    public string ApiVersion { get; set; } = "v23.0";
}

/// <summary>
/// Graph API do catálogo da Meta, com o token da loja. Fontes (consultadas em 17/10/2026):
/// <list type="bullet">
/// <item>Catalog Batch API — <c>POST /{catalog_id}/items_batch</c>, <c>item_type=PRODUCT_ITEM</c>, <c>requests[]</c> com
/// <c>method</c> CREATE/UPDATE/DELETE e <c>data</c>; <c>allow_upsert</c>; até 5000 itens; erros 80014 (muitas chamadas),
/// 100 (parâmetro), 190 (token inválido), 200 (permissão): https://developers.facebook.com/docs/marketing-api/catalog-batch/reference</item>
/// <item>Campos do item (id, title, description, availability, condition, price "9.99 BRL", link, image_link, brand,
/// quantity_to_sell_on_facebook): https://developers.facebook.com/docs/commerce-platform/catalog/fields</item>
/// </list>
/// </summary>
public sealed class MetaCatalogClient(HttpClient http, IOptions<MetaOptions> options)
{
    private string Base => $"{options.Value.GraphUrl.TrimEnd('/')}/{options.Value.ApiVersion}";

    /// <summary>Nome do catálogo (confere id e token antes de ligar).</summary>
    public async Task<string?> CatalogNameAsync(string token, string catalogId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/{Uri.EscapeDataString(catalogId)}?fields=name", token, null, ct);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return body.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
    }

    /// <summary>Atualiza (ou cria, com <c>allow_upsert</c>) itens do catálogo. Devolve os avisos de validação de cada item.</summary>
    public async Task<IReadOnlyList<string>> UpsertAsync(string token, string catalogId, IReadOnlyList<JsonObject> items, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["item_type"] = "PRODUCT_ITEM",
            ["allow_upsert"] = true,
            ["requests"] = new JsonArray(items.Select(i => (JsonNode)new JsonObject { ["method"] = "UPDATE", ["data"] = i.DeepClone() }).ToArray()),
        };
        using var response = await SendAsync(HttpMethod.Post, $"/{Uri.EscapeDataString(catalogId)}/items_batch", token, JsonContent.Create(body), ct);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return result.TryGetProperty("validation_status", out var status) && status.ValueKind == JsonValueKind.Array
            ? status.EnumerateArray()
                .SelectMany(s => s.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array ? errors.EnumerateArray() : [])
                .Select(e => e.TryGetProperty("message", out var m) ? m.GetString() : null).OfType<string>().ToList()
            : [];
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, Base + path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException e)
        {
            throw new ChannelConnectorException("Não foi possível falar com a Meta.", e);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new ChannelConnectorException("A Meta demorou demais para responder.", e);
        }
        if (response.IsSuccessStatusCode) return response;

        using (response)
        {
            var (code, message) = await ErrorAsync(response, ct);
            throw code switch
            {
                190 => new ChannelAuthorizationRevokedException("A Meta recusou o token de acesso (vencido ou revogado). Informe um token novo em Canais de venda."),
                200 => new ChannelAuthorizationRevokedException("O token não tem permissão neste catálogo. Confira o usuário do sistema no Gerenciador de Negócios."),
                100 => new ChannelValidationException(message ?? "A Meta recusou os dados enviados."),
                80014 => new ChannelConnectorException("A Meta pediu para esperar (muitas chamadas). O sistema tenta de novo."),
                _ => new ChannelConnectorException($"A Meta não concluiu o pedido ({(int)response.StatusCode})."),
            };
        }
    }

    /// <summary>Erro padrão da Graph API: <c>{"error":{"message","type","code"}}</c>. A mensagem nunca leva o token.</summary>
    private static async Task<(int Code, string? Message)> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (!body.TryGetProperty("error", out var error)) return (0, null);
            var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : 0;
            var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            return (code, message is { Length: > 300 } ? message[..300] : message);
        }
        catch (JsonException)
        {
            return (0, null);
        }
    }
}

/// <summary>
/// Graph API da Meta simulada (dev e testes): catálogo existe para qualquer id; o token <see cref="InvalidToken"/> é
/// recusado (erro 190). Guarda os itens enviados pelo items_batch, para os testes conferirem.
/// </summary>
public sealed class FakeMetaGraphApi : HttpMessageHandler
{
    public const string InvalidToken = "token-invalido-recusado-pela-meta";

    private static readonly ConcurrentDictionary<(string Catalog, string Id), JsonObject> Items = new();

    public static JsonObject? Item(string catalogId, string retailerId) => Items.TryGetValue((catalogId, retailerId), out var item) ? item : null;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization?.Parameter == InvalidToken)
            return Json(new JsonObject { ["error"] = new JsonObject { ["message"] = "Invalid OAuth access token.", ["type"] = "OAuthException", ["code"] = 190 } },
                HttpStatusCode.BadRequest);
        var segments = request.RequestUri!.AbsolutePath.Trim('/').Split('/');
        if (request.Method == HttpMethod.Get && segments.Length == 2)
            return Json(new JsonObject { ["id"] = segments[1], ["name"] = "Catálogo de teste" });
        if (request.Method == HttpMethod.Post && segments is [_, var catalog, "items_batch"])
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            foreach (var r in body["requests"]!.AsArray().OfType<JsonObject>())
            {
                var data = r["data"]!.AsObject();
                var id = (string)data["id"]!;
                var item = Items.GetOrAdd((catalog, id), _ => []);
                foreach (var (key, value) in data) item[key] = value?.DeepClone();
            }
            return Json(new JsonObject { ["handles"] = new JsonArray("SIM-HANDLE"), ["validation_status"] = new JsonArray() });
        }
        return Json(new JsonObject { ["error"] = new JsonObject { ["message"] = "Unknown path", ["code"] = 100 } }, HttpStatusCode.BadRequest);
    }

    private static HttpResponseMessage Json(JsonNode body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}
