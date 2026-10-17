using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ecommerce.Application.Channels;
using Microsoft.Extensions.Options;

namespace Ecommerce.Infrastructure.Channels.MercadoLivre;

/// <summary>
/// Chamadas à API de anúncios e categorias do ML, com o token do vendedor. Fontes (consultadas em 17/10/2026):
/// <list type="bullet">
/// <item>Categorização (preditor <c>/sites/MLB/domain_discovery/search</c>, <c>/categories/{id}</c>):
/// https://developers.mercadolivre.com.br/pt_br/categorizacao-de-produtos</item>
/// <item>Atributos (<c>/categories/{id}/attributes</c>, tags required/new_required/conditional_required/read_only/fixed):
/// https://developers.mercadolivre.com.br/pt_br/atributos</item>
/// <item>Publicar (<c>POST /items</c>; descrição em <c>POST /items/{id}/description</c>):
/// https://developers.mercadolivre.com.br/pt_br/publicacao-de-produtos; modelo User Products (<c>family_name</c> no lugar
/// de <c>title</c> para vendedores com a tag <c>user_product_seller</c>): https://developers.mercadolivre.com.br/pt_br/preco-variacao</item>
/// <item>Imagens (<c>POST /pictures/items/upload</c>, multipart, JPG/PNG): https://developers.mercadolivre.com.br/pt_br/trabalhar-com-imagens</item>
/// <item>Sincronizar (<c>PUT /items/{id}</c> com preço, quantidade e status):
/// https://developers.mercadolivre.com.br/pt_br/produto-sincronizacao-de-publicacoes</item>
/// </list>
/// Erros de validação (400) viram <see cref="ChannelValidationException"/> com os motivos do ML; fora do ar, limite de taxa
/// ou acesso recusado viram <see cref="ChannelConnectorException"/> (a fila tenta de novo).
/// </summary>
public sealed class MercadoLivreApiClient(HttpClient http, IOptions<MercadoLivreOptions> options)
{
    public const string Site = "MLB";

    private string Api => options.Value.ApiUrl.TrimEnd('/');

    public async Task<IReadOnlyList<CategoryOption>> PredictAsync(string token, string title, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/sites/{Site}/domain_discovery/search?limit=3&q={Uri.EscapeDataString(title)}", token, null, ct);
        var list = await JsonAsync(response, ct);
        return list.ValueKind != JsonValueKind.Array
            ? []
            : list.EnumerateArray().Where(e => Text(e, "category_id") is not null)
                .Select(e => new CategoryOption(Text(e, "category_id")!, Text(e, "category_name") ?? Text(e, "category_id")!, Text(e, "domain_name")))
                .DistinctBy(c => c.CategoryId).ToList();
    }

    /// <summary>Nulo se a categoria não existe.</summary>
    public async Task<CategoryDetail?> CategoryAsync(string token, string categoryId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/categories/{Uri.EscapeDataString(categoryId)}", token, null, ct, allowNotFound: true);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        var c = await JsonAsync(response, ct);
        var settings = c.TryGetProperty("settings", out var s) ? s : default;
        var path = c.TryGetProperty("path_from_root", out var p) && p.ValueKind == JsonValueKind.Array
            ? string.Join(" > ", p.EnumerateArray().Select(n => Text(n, "name")).Where(n => n is not null))
            : Text(c, "name") ?? categoryId;
        return new CategoryDetail(Text(c, "id") ?? categoryId, Text(c, "name") ?? categoryId, path,
            Int(settings, "max_title_length") ?? 60, Int(settings, "max_pictures_per_item") ?? 10,
            Bool(settings, "listing_allowed") != false && Text(settings, "status") is null or "enabled");
    }

    /// <summary>Atributos que a loja pode preencher (sem read_only, fixed e inferred).</summary>
    public async Task<IReadOnlyList<CategoryAttributeSpec>> AttributesAsync(string token, string categoryId, bool isNew, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/categories/{Uri.EscapeDataString(categoryId)}/attributes", token, null, ct);
        var list = await JsonAsync(response, ct);
        if (list.ValueKind != JsonValueKind.Array) return [];
        var result = new List<CategoryAttributeSpec>();
        foreach (var a in list.EnumerateArray())
        {
            var tags = Tags(a);
            if (Text(a, "id") is not { } id || tags.Overlaps(["read_only", "fixed", "inferred", "others"])) continue;
            var values = a.TryGetProperty("values", out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Where(x => Text(x, "id") is not null && Text(x, "name") is not null)
                    .Select(x => new AttributeValueOption(Text(x, "id")!, Text(x, "name")!)).ToList()
                : [];
            var units = a.TryGetProperty("allowed_units", out var u) && u.ValueKind == JsonValueKind.Array
                ? u.EnumerateArray().Select(x => Text(x, "id")).OfType<string>().ToList()
                : [];
            result.Add(new CategoryAttributeSpec(id, Text(a, "name") ?? id, Text(a, "value_type") ?? "string",
                tags.Contains("required") || (isNew && tags.Contains("new_required")), tags.Contains("conditional_required"), tags.Contains("multivalued"),
                Int(a, "value_max_length"), values, units, Text(a, "default_unit")));
        }
        return result;
    }

    /// <summary>Vendedor no modelo User Products: publica com <c>family_name</c> e sem <c>title</c>.</summary>
    public async Task<bool> IsUserProductSellerAsync(string token, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, "/users/me", token, null, ct);
        return Tags(await JsonAsync(response, ct)).Contains("user_product_seller");
    }

    /// <returns>Id da imagem no ML.</returns>
    public async Task<string> UploadPictureAsync(string token, byte[] jpeg, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(jpeg);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", "foto.jpg");
        using var response = await SendAsync(HttpMethod.Post, "/pictures/items/upload", token, form, ct);
        return Text(await JsonAsync(response, ct), "id") ?? throw new ChannelConnectorException("O Mercado Livre não devolveu o id da imagem.");
    }

    /// <returns>Id do anúncio (ex.: MLB1234567890).</returns>
    public async Task<string> CreateItemAsync(string token, JsonObject item, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, "/items", token, JsonContent.Create(item), ct);
        return Text(await JsonAsync(response, ct), "id") ?? throw new ChannelConnectorException("O Mercado Livre não devolveu o id do anúncio.");
    }

    public async Task SetDescriptionAsync(string token, string itemId, string text, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, $"/items/{Uri.EscapeDataString(itemId)}/description", token,
            JsonContent.Create(new JsonObject { ["plain_text"] = text }), ct);
    }

    /// <returns>Avisos do ML (ex.: preço ignorado porque o anúncio tem automação de preço ligada).</returns>
    public async Task<IReadOnlyList<string>> UpdateItemAsync(string token, string itemId, JsonObject changes, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Put, $"/items/{Uri.EscapeDataString(itemId)}", token, JsonContent.Create(changes), ct);
        var body = await JsonAsync(response, ct);
        return body.TryGetProperty("warnings", out var w) && w.ValueKind == JsonValueKind.Array
            ? w.EnumerateArray().Select(x => Text(x, "message") ?? x.GetRawText()).ToList()
            : [];
    }

    /// <summary>Nulo se o anúncio não existe (404).</summary>
    public async Task<JsonElement?> GetItemAsync(string token, string itemId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/items/{Uri.EscapeDataString(itemId)}", token, null, ct, allowNotFound: true);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return await JsonAsync(response, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, HttpContent? content, CancellationToken ct, bool allowNotFound = false)
    {
        using var request = new HttpRequestMessage(method, Api + path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException e)
        {
            throw new ChannelConnectorException("Não foi possível falar com o Mercado Livre.", e);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new ChannelConnectorException("O Mercado Livre demorou demais para responder.", e);
        }
        if (response.IsSuccessStatusCode || (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)) return response;

        using (response)
        {
            var status = (int)response.StatusCode;
            if (status is 400 or 422 && await ReasonsAsync(response, ct) is { Length: > 0 } reasons)
                throw new ChannelValidationException(reasons);
            throw status switch
            {
                401 or 403 => new ChannelConnectorException($"O Mercado Livre recusou o acesso ({status}). Confira a conexão em Canais de venda."),
                404 => new ChannelConnectorException("O anúncio não existe mais no Mercado Livre."),
                429 => new ChannelConnectorException("O Mercado Livre pediu para esperar (muitas chamadas). O sistema tenta de novo."),
                _ => new ChannelConnectorException($"O Mercado Livre não concluiu o pedido ({status})."),
            };
        }
    }

    /// <summary>Motivos do ML: <c>cause[].message</c> (ou a mensagem geral). Nunca leva o token.</summary>
    private static async Task<string> ReasonsAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var causes = body.TryGetProperty("cause", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Object ? Text(x, "message") : null).OfType<string>().ToList()
                : [];
            var text = causes.Count > 0 ? string.Join(" ", causes) : Text(body, "message") ?? Text(body, "error") ?? string.Empty;
            return text.Length > 450 ? text[..450] : text;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        }
        catch (JsonException e)
        {
            throw new ChannelConnectorException("Resposta do Mercado Livre em formato inesperado.", e);
        }
    }

    internal static HashSet<string> Tags(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("tags", out var tags)) return [];
        // As tags vêm como lista (["required"]) ou como objeto ({"required": true}), dependendo do recurso.
        return tags.ValueKind switch
        {
            JsonValueKind.Array => tags.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).ToHashSet(StringComparer.Ordinal),
            JsonValueKind.Object => tags.EnumerateObject().Where(t => t.Value.ValueKind == JsonValueKind.True).Select(t => t.Name).ToHashSet(StringComparer.Ordinal),
            _ => [],
        };
    }

    internal static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null }
            : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}
