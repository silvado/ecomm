using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ecommerce.Infrastructure.Channels.MercadoLivre;

/// <summary>
/// "Servidor" do Mercado Livre em memória (dev e testes; <c>MercadoLivre:UseFake</c>): o cliente HTTP real fala com ele.
/// Responde nos formatos das páginas citadas em <see cref="MercadoLivreApiClient"/>, com três categorias de exemplo de
/// autopeças (ids e nomes de exemplo, não os do ML). O vendedor está no modelo User Products: anúncio com <c>title</c> é
/// recusado. Anúncio desconhecido neste processo (criado pela API e lido pelo Worker, no compose) é tratado como existente.
/// </summary>
public sealed class FakeMercadoLivreApi : HttpMessageHandler
{
    public const string HeadlightCategory = "MLB-SIM-FAROL";
    public const string MirrorCategory = "MLB-SIM-RETROVISOR";
    public const string OtherCategory = "MLB-SIM-OUTROS";

    /// <summary>No family_name: o ML recusa o anúncio (para testar a mensagem).</summary>
    public const string RefuseMarker = "[recusar]";

    private static readonly string[] RequiredAttributes = ["BRAND", "ITEM_CONDITION", "PART_NUMBER"];
    private static readonly ConcurrentDictionary<string, JsonObject> Items = new();
    private static readonly ConcurrentDictionary<string, string> Descriptions = new();
    private static int _pictures;

    private static readonly Dictionary<string, (string Name, string Path)> Categories = new(StringComparer.Ordinal)
    {
        [HeadlightCategory] = ("Faróis", "Acessórios para Veículos > Peças de Carros > Iluminação > Faróis"),
        [MirrorCategory] = ("Retrovisores", "Acessórios para Veículos > Peças de Carros > Carroceria > Retrovisores"),
        [OtherCategory] = ("Outras peças", "Acessórios para Veículos > Peças de Carros > Outras"),
    };

    /// <summary>Catálogo de veículos de exemplo (marca → modelo → motores), no formato do top_values.</summary>
    private static readonly Dictionary<(string Id, string Name), Dictionary<(string Id, string Name), string[]>> Vehicles = new()
    {
        [("60249", "Volkswagen")] = new()
        {
            [("62109", "Gol")] = ["1.0 8V", "1.6 8V", "1.0 12V"],
            [("SIM-FOX", "Fox")] = ["1.0 8V", "1.6 8V"],
            [("SIM-VOY", "Voyage")] = ["1.6 8V"],
        },
        [("67781", "Fiat")] = new()
        {
            [("SIM-UNO", "Uno")] = ["1.0 Fire", "1.0 Evo"],
            [("SIM-STR", "Strada")] = ["1.3 Firefly", "1.4 Fire"],
        },
    };

    private static readonly ConcurrentDictionary<string, List<JsonObject>> Compatible = new();

    /// <summary>Famílias de veículos ligadas ao anúncio (o que foi enviado em products_families).</summary>
    public static IReadOnlyList<JsonObject> CompatibilitiesOf(string itemId) => Compatible.TryGetValue(itemId, out var list) ? list : [];

    /// <summary>O que foi publicado (para os testes conferirem o corpo enviado).</summary>
    public static JsonObject? Item(string itemId) => Items.TryGetValue(itemId, out var item) ? item : null;

    public static string? Description(string itemId) => Descriptions.TryGetValue(itemId, out var d) ? d : null;

    /// <summary>Simula alguém mexendo no anúncio direto no ML.</summary>
    public static void Change(string itemId, string? status = null, int? quantity = null, decimal? price = null)
    {
        var item = Items.GetOrAdd(itemId, Unknown);
        if (status is not null) item["status"] = status;
        if (quantity is not null) item["available_quantity"] = quantity;
        if (price is not null) item["price"] = price;
    }

    public static void Remove(string itemId) => Items[itemId] = new JsonObject { ["id"] = itemId, ["removed"] = true };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        var segments = path.Trim('/').Split('/');
        var method = request.Method.Method;

        return (method, segments) switch
        {
            ("GET", ["users", "me"]) => Json(new JsonObject { ["id"] = 1, ["nickname"] = "VENDEDOR_TESTE", ["tags"] = new JsonArray("normal", "user_product_seller") }),
            ("GET", ["sites", "MLB", "domain_discovery", "search"]) => Json(Predict(query["q"] ?? string.Empty)),
            ("GET", ["categories", var id]) => Categories.TryGetValue(id, out var c) ? Json(Category(id, c.Name, c.Path)) : NotFound(),
            ("GET", ["categories", var id, "attributes"]) => Categories.ContainsKey(id) ? Json(Attributes()) : NotFound(),
            ("POST", ["pictures", "items", "upload"]) => Json(new JsonObject { ["id"] = $"SIM-{Interlocked.Increment(ref _pictures)}-MLB" }),
            ("POST", ["items"]) => Create(await BodyAsync(request, cancellationToken)),
            ("POST", ["items", var id, "description"]) => Describe(id, await BodyAsync(request, cancellationToken)),
            ("PUT", ["items", var id]) => Update(id, await BodyAsync(request, cancellationToken)),
            ("GET", ["items", var id]) => Get(id),
            ("POST", ["catalog_domains", MercadoLivreApiClient.VehicleDomain, "attributes", var attribute, "top_values"]) =>
                Json(TopValues(attribute, await BodyAsync(request, cancellationToken))),
            ("POST", ["items", var id, "compatibilities"]) => Compatibilities(id, await BodyAsync(request, cancellationToken)),
            _ => NotFound(),
        };
    }

    private static JsonArray Predict(string q)
    {
        var text = q.ToLowerInvariant();
        var id = text.Contains("farol", StringComparison.Ordinal) ? HeadlightCategory
            : text.Contains("retrovisor", StringComparison.Ordinal) ? MirrorCategory
            : OtherCategory;
        var others = Categories.Keys.Where(k => k != id).Take(2);
        return new JsonArray(new[] { id }.Concat(others).Select(k => (JsonNode)new JsonObject
        {
            ["domain_id"] = "MLB-SIM-" + k.Split('-').Last(), ["domain_name"] = Categories[k].Name, ["category_id"] = k, ["category_name"] = Categories[k].Name,
            ["attributes"] = new JsonArray(),
        }).ToArray());
    }

    private static JsonObject Category(string id, string name, string path) => new()
    {
        ["id"] = id,
        ["name"] = name,
        ["path_from_root"] = new JsonArray(path.Split(" > ").Select(p => (JsonNode)new JsonObject { ["id"] = id, ["name"] = p }).ToArray()),
        ["settings"] = new JsonObject { ["listing_allowed"] = true, ["status"] = "enabled", ["max_title_length"] = 60, ["max_pictures_per_item"] = 12 },
    };

    private static JsonArray Attributes() =>
    [
        Attribute("BRAND", "Marca", "string", ["required"]),
        Attribute("ITEM_CONDITION", "Condição do item", "list", ["required", "hidden"], [("2230284", "Novo"), ("2230581", "Usado"), ("2230582", "Recondicionado")]),
        Attribute("PART_NUMBER", "Número de peça", "string", ["required"]),
        Attribute("POSITION", "Posição", "list", [], [("SIM-1", "Dianteira"), ("SIM-2", "Traseira")]),
        Attribute("SIDE", "Lado", "list", [], [("SIM-3", "Esquerdo"), ("SIM-4", "Direito")]),
        Attribute("GTIN", "Código universal de produto", "string", ["conditional_required"]),
        Attribute("SELLER_SKU", "SKU", "string", ["hidden"]),
        Attribute("PRODUCT_DATA_SOURCE", "Origem dos dados", "string", ["read_only"]),
    ];

    private static JsonObject Attribute(string id, string name, string type, string[] tags, (string Id, string Name)[]? values = null) => new()
    {
        ["id"] = id,
        ["name"] = name,
        ["value_type"] = type,
        ["value_max_length"] = 255,
        ["tags"] = new JsonObject(tags.Select(t => KeyValuePair.Create(t, (JsonNode?)JsonValue.Create(true)))),
        ["values"] = new JsonArray((values ?? []).Select(v => (JsonNode)new JsonObject { ["id"] = v.Id, ["name"] = v.Name }).ToArray()),
    };

    private static HttpResponseMessage Create(JsonObject body)
    {
        if (body.ContainsKey("title"))
            return Error("O vendedor está no modelo de User Products: envie family_name em vez de title.");
        var name = (string?)body["family_name"] ?? string.Empty;
        if (name.Length == 0) return Error("family_name é obrigatório.");
        if (name.Contains(RefuseMarker, StringComparison.Ordinal)) return Error("O título contém palavras não permitidas.");
        var category = (string?)body["category_id"] ?? string.Empty;
        if (!Categories.ContainsKey(category)) return Error($"Category {category} does not exist");
        var attributes = (body["attributes"] as JsonArray ?? []).OfType<JsonObject>().Select(a => (string?)a["id"]).ToHashSet();
        var missing = RequiredAttributes.Where(a => !attributes.Contains(a)).ToList();
        if (missing.Count > 0) return Error($"The attributes [{string.Join(", ", missing)}] are required for category {category}.");
        if ((decimal?)body["price"] is not > 0) return Error("price must be positive.");
        if ((body["pictures"] as JsonArray)?.Count is not > 0) return Error("pictures is required.");

        var id = "MLB" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var item = (JsonObject)body.DeepClone();
        item["id"] = id;
        item["title"] = name;
        item["status"] = (int?)body["available_quantity"] > 0 ? "active" : "paused";
        Items[id] = item;
        return Json(new JsonObject { ["id"] = id, ["status"] = item["status"]!.DeepClone(), ["family_name"] = name, ["title"] = name });
    }

    private static HttpResponseMessage Describe(string id, JsonObject body)
    {
        Descriptions[id] = (string?)body["plain_text"] ?? string.Empty;
        return Json(new JsonObject { ["text"] = string.Empty, ["plain_text"] = Descriptions[id] }, HttpStatusCode.Created);
    }

    private static HttpResponseMessage Update(string id, JsonObject body)
    {
        var item = Items.GetOrAdd(id, Unknown);
        if (item.ContainsKey("removed")) return NotFound();
        foreach (var (key, value) in body)
        {
            if (key is "status" && (string?)value is not ("active" or "paused" or "closed")) return Error("status inválido.");
            item[key] = value?.DeepClone();
        }
        return Json((JsonObject)item.DeepClone());
    }

    private static HttpResponseMessage Get(string id)
    {
        var item = Items.GetOrAdd(id, Unknown);
        return item.ContainsKey("removed") ? NotFound() : Json((JsonObject)item.DeepClone());
    }

    private static JsonArray TopValues(string attribute, JsonObject body)
    {
        var known = (body["known_attributes"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(k => (string)k["id"]!, k => (string?)k["value_id"]);
        var brand = Vehicles.Keys.FirstOrDefault(b => known.TryGetValue("BRAND", out var id) && id == b.Id);
        var models = brand.Id is null ? null : Vehicles[brand];
        var model = models?.Keys.FirstOrDefault(m => known.TryGetValue("MODEL", out var id) && id == m.Id);
        IEnumerable<(string Id, string Name)> values = attribute switch
        {
            "BRAND" => Vehicles.Keys,
            "MODEL" => models is null ? [] : models.Keys,
            "ENGINE" when models is not null && model is { Id: not null } m => models[m].Select((e, i) => ($"SIM-ENG-{m.Id}-{i}", e)),
            "VEHICLE_YEAR" => Enumerable.Range(1990, 37).Select(y => ($"SIM-Y{y}", y.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            _ => [],
        };
        return new JsonArray(values.Select(v => (JsonNode)new JsonObject { ["id"] = v.Id, ["name"] = v.Name, ["metric"] = 1 }).ToArray());
    }

    private static HttpResponseMessage Compatibilities(string id, JsonObject body)
    {
        if (!Items.ContainsKey(id)) return NotFound();
        var families = (body["products_families"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (families.Count == 0 || families.Count > 10) return Error("Mais de 10 domínios de produto foram especificados.");
        if (families.Any(f => (string?)f["creation_source"] is null)) return Error("creation_source é obrigatório.");
        if (families.Any(f => (string?)f["domain_id"] != MercadoLivreApiClient.VehicleDomain)) return Error("domain_id inválido.");
        Compatible.AddOrUpdate(id, _ => families.Select(f => (JsonObject)f.DeepClone()).ToList(), (_, list) => [.. list, .. families.Select(f => (JsonObject)f.DeepClone())]);
        return Json(new JsonObject { ["created_compatibilities_count"] = families.Count * 3 }, HttpStatusCode.Created);
    }

    private static JsonObject Unknown(string id) => new() { ["id"] = id, ["status"] = "active", ["available_quantity"] = 0, ["price"] = 0 };

    private static async Task<JsonObject> BodyAsync(HttpRequestMessage request, CancellationToken ct) =>
        request.Content is null ? [] : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct)) as JsonObject ?? [];

    private static HttpResponseMessage Error(string message) => Json(new JsonObject
    {
        ["message"] = "Validation error", ["error"] = "validation_error", ["status"] = 400,
        ["cause"] = new JsonArray(new JsonObject { ["code"] = "item.validation", ["type"] = "error", ["message"] = message }),
    }, HttpStatusCode.BadRequest);

    private static HttpResponseMessage NotFound() => Json(new JsonObject { ["message"] = "not_found", ["status"] = 404 }, HttpStatusCode.NotFound);

    private static HttpResponseMessage Json(JsonNode body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}
