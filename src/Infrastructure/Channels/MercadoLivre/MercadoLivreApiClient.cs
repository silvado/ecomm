using System.Globalization;
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
/// <item>Perguntas (<c>GET /questions/{id}</c>, <c>POST /answers</c>): https://developers.mercadolivre.com.br/pt_br/perguntas-e-respostas</item>
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

    /// <summary>Domínio de veículos do Brasil para compatibilidades (referencias-de-dominios-produtos-e-atributos-para-autopecas).</summary>
    public const string VehicleDomain = Site + "-CARS_AND_VANS";

    /// <summary>
    /// Valores de um atributo do catálogo de veículos, filtrados pelos já conhecidos (ex.: modelos de uma marca):
    /// <c>POST /catalog_domains/{domínio}/attributes/{atributo}/top_values</c>.
    /// </summary>
    public async Task<IReadOnlyList<AttributeValueOption>> TopValuesAsync(string token, string attributeId, IReadOnlyList<(string Id, string ValueId)> known, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["known_attributes"] = new JsonArray(known.Select(k => (JsonNode)new JsonObject { ["id"] = k.Id, ["value_id"] = k.ValueId }).ToArray()),
        };
        using var response = await SendAsync(HttpMethod.Post, $"/catalog_domains/{VehicleDomain}/attributes/{Uri.EscapeDataString(attributeId)}/top_values", token,
            JsonContent.Create(body), ct);
        var list = await JsonAsync(response, ct);
        return list.ValueKind != JsonValueKind.Array
            ? []
            : list.EnumerateArray().Where(v => Text(v, "id") is not null && Text(v, "name") is not null)
                .Select(v => new AttributeValueOption(Text(v, "id")!, Text(v, "name")!)).ToList();
    }

    /// <summary>
    /// Acrescenta compatibilidades ao anúncio (as já cadastradas ficam): <c>POST /items/{id}/compatibilities</c> com
    /// <c>products_families</c> (até 10 por chamada e 200 veículos no total). Fonte:
    /// https://developers.mercadolivre.com.br/pt_br/compatibilidades-itens-e-produtos-de-autopecas
    /// </summary>
    /// <returns>Quantos veículos o ML ligou ao anúncio.</returns>
    public async Task<int> AddCompatibilitiesAsync(string token, string itemId, JsonObject body, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, $"/items/{Uri.EscapeDataString(itemId)}/compatibilities", token, JsonContent.Create(body), ct);
        var result = await JsonAsync(response, ct);
        return result.TryGetProperty("created_compatibilities_count", out var count) && count.TryGetInt32(out var n) ? n : 0;
    }

    /// <summary>
    /// Pedido do vendedor (<c>GET /orders/{id}</c>): status, itens (anúncio, quantidade, preço), pagamentos e comprador.
    /// Fonte: https://developers.mercadolivre.com.br/pt_br/gerenciamento-de-vendas. Nulo se não existe.
    /// </summary>
    public async Task<ChannelOrderSnapshot?> GetOrderAsync(string token, string orderId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/orders/{Uri.EscapeDataString(orderId)}", token, null, ct, allowNotFound: true);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        var order = await JsonAsync(response, ct);
        var items = order.TryGetProperty("order_items", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(i =>
            {
                var item = i.TryGetProperty("item", out var it) ? it : default;
                return new ChannelOrderItem(Text(item, "id") ?? string.Empty, Text(item, "title") ?? string.Empty,
                    i.TryGetProperty("quantity", out var q) && q.TryGetInt32(out var qi) ? qi : 0,
                    i.TryGetProperty("unit_price", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDecimal() : 0m);
            }).Where(i => i.ExternalItemId.Length > 0 && i.Quantity > 0).ToList()
            : [];
        var shipping = order.TryGetProperty("payments", out var payments) && payments.ValueKind == JsonValueKind.Array
            ? payments.EnumerateArray().Where(p => Text(p, "status") == "approved")
                .Sum(p => p.TryGetProperty("shipping_cost", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDecimal() : 0m)
            : 0m;
        var buyer = order.TryGetProperty("buyer", out var b) ? Text(b, "nickname") ?? (Text(b, "id") is { } id ? $"Comprador {id} no Mercado Livre" : null) : null;
        DateTimeOffset? paidAt = Text(order, "date_closed") is { } closed && DateTimeOffset.TryParse(closed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? at.ToUniversalTime() : null;
        var shipmentId = order.TryGetProperty("shipping", out var s2) ? Text(s2, "id") : null;
        return new ChannelOrderSnapshot(Text(order, "id") ?? orderId, Text(order, "status") ?? string.Empty, buyer, shipping, paidAt, items, shipmentId);
    }

    /// <summary>
    /// Envio (<c>GET /shipments/{id}</c>, cabeçalho <c>x-format-new: true</c> obrigatório desde 12/10/2025):
    /// https://developers.mercadolivre.com.br/pt_br/gerenciamento-de-envios. Nulo se não existe.
    /// </summary>
    public async Task<ChannelShipmentSnapshot?> GetShipmentAsync(string token, string shipmentId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/shipments/{Uri.EscapeDataString(shipmentId)}", token, null, ct, allowNotFound: true,
            headers: [("x-format-new", "true")]);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        var shipment = await JsonAsync(response, ct);
        return new ChannelShipmentSnapshot(Text(shipment, "id") ?? shipmentId, Text(shipment, "status") ?? string.Empty, Text(shipment, "substatus"),
            Text(shipment, "tracking_number"));
    }

    /// <summary>
    /// Avisos que o ML não conseguiu entregar (guardados por 2 dias): <c>GET /missed_feeds?app_id=…&amp;topic=…</c>.
    /// Devolve o corpo original de cada aviso (<c>request.data</c>), para reentrar pelo mesmo caminho do webhook. Fonte:
    /// https://developers.mercadolivre.com.br/pt_br/produto-receba-notificacoes (Histórico das notificações).
    /// </summary>
    public async Task<IReadOnlyList<string>> MissedFeedsAsync(string token, string appId, string topic, int offset, int limit, CancellationToken ct)
    {
        var site = topic == "items" ? $"&site_id={Site}" : string.Empty;
        using var response = await SendAsync(HttpMethod.Get,
            $"/missed_feeds?app_id={Uri.EscapeDataString(appId)}&topic={Uri.EscapeDataString(topic)}{site}&offset={offset}&limit={limit}", token, null, ct);
        var body = await JsonAsync(response, ct);
        return body.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array
            ? messages.EnumerateArray()
                .Select(m => m.TryGetProperty("request", out var r) && r.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null)
                .OfType<string>().ToList()
            : [];
    }

    /// <summary>
    /// Pergunta (<c>GET /questions/{id}?api_version=4</c>): anúncio, situação, texto e resposta. Os dados do comprador não
    /// são lidos (LGPD). Fonte: https://developers.mercadolivre.com.br/pt_br/perguntas-e-respostas (atualizada em
    /// 05/06/2025, consultada em 18/10/2026). Nulo se não existe.
    /// </summary>
    public async Task<ChannelQuestionSnapshot?> GetQuestionAsync(string token, string questionId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/questions/{Uri.EscapeDataString(questionId)}?api_version=4", token, null, ct, allowNotFound: true);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return Question(await JsonAsync(response, ct), questionId);
    }

    /// <summary>Responde a pergunta (<c>POST /answers</c> com <c>question_id</c> e <c>text</c>); devolve a pergunta respondida.</summary>
    public async Task<ChannelQuestionSnapshot> AnswerQuestionAsync(string token, string questionId, string text, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["question_id"] = long.TryParse(questionId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : questionId,
            ["text"] = text,
        };
        using var response = await SendAsync(HttpMethod.Post, "/answers", token, JsonContent.Create(body), ct);
        return Question(await JsonAsync(response, ct), questionId);
    }

    private static ChannelQuestionSnapshot Question(JsonElement q, string questionId)
    {
        var answer = q.TryGetProperty("answer", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
        return new ChannelQuestionSnapshot(Text(q, "id") ?? questionId, Text(q, "item_id") ?? string.Empty, Text(q, "status") ?? string.Empty,
            Text(q, "text") ?? string.Empty, Date(q, "date_created"),
            Text(answer, "status") == "ACTIVE" ? Text(answer, "text") : null, Date(answer, "date_created"));
    }

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        Text(e, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at.ToUniversalTime() : null;

    /// <summary>Nulo se o anúncio não existe (404).</summary>
    public async Task<JsonElement?> GetItemAsync(string token, string itemId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/items/{Uri.EscapeDataString(itemId)}", token, null, ct, allowNotFound: true);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return await JsonAsync(response, ct);
    }

    /// <summary>Tamanho da página da lista de anúncios do vendedor.</summary>
    public const int SearchPageSize = 100;

    /// <summary>
    /// Uma página dos anúncios do vendedor (RF11 CA2): <c>GET /users/{id}/items/search</c> no modo <c>search_type=scan</c>
    /// (permite passar de 1.000 anúncios), seguindo o <c>scroll_id</c>. Fonte: "Itens e buscas"
    /// (https://developers.mercadolivre.com.br/itens-e-buscas). HIPÓTESE até testar com o app real: a resposta traz os ids
    /// em <c>results</c> e o cursor em <c>scroll_id</c>; página vazia encerra.
    /// </summary>
    public async Task<(IReadOnlyList<string> Ids, string? NextCursor)> SearchSellerItemsAsync(string token, string sellerId, string? cursor, CancellationToken ct)
    {
        var path = $"/users/{Uri.EscapeDataString(sellerId)}/items/search?search_type=scan&limit={SearchPageSize.ToString(CultureInfo.InvariantCulture)}";
        if (!string.IsNullOrEmpty(cursor)) path += "&scroll_id=" + Uri.EscapeDataString(cursor);
        using var response = await SendAsync(HttpMethod.Get, path, token, null, ct);
        var body = await JsonAsync(response, ct);
        var ids = body.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
            ? results.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.String).Select(r => r.GetString()!).ToList()
            : [];
        return (ids, Text(body, "scroll_id"));
    }

    /// <summary>
    /// O anúncio completo para a importação (RF11 CA2): <c>GET /items/{id}</c> e a descrição em
    /// <c>GET /items/{id}/description</c> (texto simples; sem descrição, segue sem). Nulo se o anúncio não existe.
    /// Fontes: https://developers.mercadolivre.com.br/itens-e-buscas e https://developers.mercadolivre.com.br/pt_br/publicacao-de-produtos.
    /// </summary>
    public async Task<ChannelItemSnapshot?> GetItemSnapshotAsync(string token, string itemId, CancellationToken ct)
    {
        if (await GetItemAsync(token, itemId, ct) is not { } item) return null;
        string? description = null;
        try
        {
            using var response = await SendAsync(HttpMethod.Get, $"/items/{Uri.EscapeDataString(itemId)}/description", token, null, ct, allowNotFound: true);
            if (response.StatusCode != HttpStatusCode.NotFound) description = Text(await JsonAsync(response, ct), "plain_text");
        }
        catch (ChannelConnectorException)
        {
            // Sem descrição: a peça entra sem ela.
        }
        return Snapshot(item, description);
    }

    internal static ChannelItemSnapshot Snapshot(JsonElement item, string? description)
    {
        static IReadOnlyList<AttributeValue> Values(JsonElement e, string name) =>
            e.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object && Text(a, "id") is not null)
                    .Select(a => new AttributeValue(Text(a, "id")!, Text(a, "value_id"), Text(a, "value_name"))).ToList()
                : [];
        var pictures = item.TryGetProperty("pictures", out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Select(x => Text(x, "secure_url") ?? Text(x, "url")).OfType<string>().ToList()
            : [];
        var title = Text(item, "title") ?? Text(item, "family_name") ?? string.Empty;
        var price = item.TryGetProperty("price", out var pr) && pr.ValueKind == JsonValueKind.Number ? pr.GetDecimal() : 0m;
        var quantity = item.TryGetProperty("available_quantity", out var q) && q.TryGetInt32(out var qi) ? qi : 0;
        var variations = item.TryGetProperty("variations", out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0;
        return new ChannelItemSnapshot(Text(item, "id") ?? string.Empty, title, price, quantity, Text(item, "status") ?? string.Empty, Text(item, "condition"),
            Text(item, "seller_custom_field"), description, pictures, Text(item, "category_id"), Text(item, "listing_type_id"),
            Values(item, "attributes"), Values(item, "sale_terms"), variations);
    }

    public const int MaxPictureBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Baixa uma foto do anúncio para a peça. Só do servidor de imagens do ML (<c>https://*.mlstatic.com</c>): o endereço vem
    /// da resposta do canal e não pode levar o servidor a outros destinos. Nulo se não der (a peça segue sem a foto).
    /// </summary>
    public async Task<byte[]?> DownloadPictureAsync(string url, CancellationToken ct)
    {
        if (!IsPictureUrl(url)) return null;
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxPictureBytes) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > MaxPictureBytes) return null;
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public static bool IsPictureUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
        && (uri.Host.Equals("mlstatic.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".mlstatic.com", StringComparison.OrdinalIgnoreCase));

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, HttpContent? content, CancellationToken ct, bool allowNotFound = false,
        (string Name, string Value)[]? headers = null)
    {
        using var request = new HttpRequestMessage(method, Api + path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        foreach (var (name, value) in headers ?? []) request.Headers.Add(name, value);
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
