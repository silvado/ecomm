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
    private static readonly ConcurrentDictionary<string, JsonObject> Orders = new();
    private static readonly ConcurrentDictionary<string, JsonObject> Shipments = new();
    private static readonly ConcurrentQueue<(string Topic, string Data)> Missed = new();

    /// <summary>Envio da venda simulada: muda a situação (shipped, delivered, not_delivered...) e o rastreio.</summary>
    public static string? ShipmentOf(string orderId) => Orders.TryGetValue(orderId, out var o) ? (string?)o["shipping"]?["id"]?.ToString() : null;

    public static void SetShipment(string shipmentId, string status, string? tracking = null, string? substatus = null)
    {
        var shipment = Shipments.GetOrAdd(shipmentId, id => new JsonObject { ["id"] = long.Parse(id, System.Globalization.CultureInfo.InvariantCulture) });
        shipment["status"] = status;
        shipment["substatus"] = substatus;
        if (tracking is not null) shipment["tracking_number"] = tracking;
    }

    /// <summary>Aviso que "não chegou" à plataforma: fica no missed_feeds do app (corpo original do aviso).</summary>
    public static void Miss(string topic, string data) => Missed.Enqueue((topic, data));

    private static JsonObject MissedFeeds(string topic, int offset, int limit) => new()
    {
        ["messages"] = new JsonArray(Missed.Where(m => m.Topic == topic).Skip(offset).Take(limit)
            .Select(m => (JsonNode)new JsonObject { ["topic"] = topic, ["request"] = new JsonObject { ["data"] = m.Data } }).ToArray()),
    };

    /// <summary>
    /// Simula uma venda paga no ML (formato de https://developers.mercadolivre.com.br/pt_br/gerenciamento-de-vendas): baixa
    /// a quantidade do anúncio aqui, como o ML faz, e devolve o id do pedido (para mandar o aviso orders_v2).
    /// </summary>
    public static string Sell(string itemId, int quantity, string sellerId, decimal? unitPrice = null, string title = "Peça vendida no ML")
    {
        var item = Items.GetOrAdd(itemId, Unknown);
        var price = unitPrice ?? (item["price"] is { } p && decimal.TryParse(p.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture, out var stored) && stored > 0 ? stored : 100m);
        if (item["available_quantity"] is { } q && int.TryParse(q.ToJsonString(), out var left)) item["available_quantity"] = Math.Max(0, left - quantity);
        var id = Random.Shared.NextInt64(2_000_000_000_000_000, 2_999_999_999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var shipmentId = Random.Shared.NextInt64(40_000_000_000, 49_999_999_999);
        SetShipment(shipmentId.ToString(System.Globalization.CultureInfo.InvariantCulture), "ready_to_ship");
        Orders[id] = new JsonObject
        {
            ["id"] = long.Parse(id, System.Globalization.CultureInfo.InvariantCulture),
            ["status"] = "paid",
            ["date_closed"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", System.Globalization.CultureInfo.InvariantCulture),
            ["total_amount"] = price * quantity,
            ["order_items"] = new JsonArray(new JsonObject
            {
                ["item"] = new JsonObject { ["id"] = itemId, ["title"] = title },
                ["quantity"] = quantity,
                ["unit_price"] = price,
                ["currency_id"] = "BRL",
            }),
            ["payments"] = new JsonArray(new JsonObject { ["status"] = "approved", ["shipping_cost"] = 25.9m, ["total_paid_amount"] = price * quantity + 25.9m }),
            ["buyer"] = new JsonObject { ["id"] = 266272126, ["nickname"] = "COMPRADOR_TESTE" },
            ["shipping"] = new JsonObject { ["id"] = shipmentId },
            ["seller"] = new JsonObject { ["id"] = long.Parse(sellerId, System.Globalization.CultureInfo.InvariantCulture) },
            ["tags"] = new JsonArray("paid", "test_order"),
        };
        return id;
    }

    private static readonly ConcurrentDictionary<string, JsonObject> Questions = new();

    /// <summary>
    /// Simula um comprador perguntando no anúncio (formato de https://developers.mercadolivre.com.br/pt_br/perguntas-e-respostas);
    /// devolve o id da pergunta (para mandar o aviso questions).
    /// </summary>
    public static string Ask(string itemId, string sellerId, string text)
    {
        var id = Random.Shared.NextInt64(10_000_000_000, 19_999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Questions[id] = new JsonObject
        {
            ["id"] = long.Parse(id, System.Globalization.CultureInfo.InvariantCulture),
            ["answer"] = null,
            ["date_created"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", System.Globalization.CultureInfo.InvariantCulture),
            ["deleted_from_listing"] = false,
            ["hold"] = false,
            ["item_id"] = itemId,
            ["seller_id"] = long.Parse(sellerId, System.Globalization.CultureInfo.InvariantCulture),
            ["status"] = "UNANSWERED",
            ["text"] = text,
            ["from"] = new JsonObject { ["id"] = 207119838, ["answered_questions"] = 0 },
        };
        return id;
    }

    /// <summary>A pergunta como está no ML simulado (para os testes conferirem a resposta enviada).</summary>
    public static JsonObject? Question(string questionId) => Questions.TryGetValue(questionId, out var q) ? q : null;

    /// <summary>Simula a loja respondendo direto no site do ML, ou o ML excluindo a pergunta (status DELETED, BANNED...).</summary>
    public static void ChangeQuestion(string questionId, string status, string? answer = null)
    {
        if (!Questions.TryGetValue(questionId, out var q)) return;
        q["status"] = status;
        if (answer is not null) q["answer"] = Answer(answer);
    }

    private static JsonObject Answer(string text) => new()
    {
        ["date_created"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", System.Globalization.CultureInfo.InvariantCulture),
        ["status"] = "ACTIVE",
        ["text"] = text,
    };

    private static HttpResponseMessage AnswerQuestion(JsonObject body)
    {
        var id = body["question_id"]?.ToJsonString().Trim('"') ?? string.Empty;
        if (!Questions.TryGetValue(id, out var q)) return NotFound();
        var text = (string?)body["text"] ?? string.Empty;
        if (text.Trim().Length == 0) return Error("text is required.");
        if ((string?)q["status"] != "UNANSWERED") return Error("The question is not unanswered.");
        q["status"] = "ANSWERED";
        q["answer"] = Answer(text);
        return Json((JsonObject)q.DeepClone());
    }

    /// <summary>Simula o cancelamento da venda no ML.</summary>
    public static void CancelOrder(string orderId)
    {
        if (Orders.TryGetValue(orderId, out var order)) order["status"] = "cancelled";
    }

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

    /// <summary>Endereço de foto que o servidor de imagens simulado não encontra (para testar a peça sem foto).</summary>
    public const string MissingPicture = "https://http2.mlstatic.com/D_SIM_SEM_FOTO.jpg";

    /// <summary>
    /// Anúncio que o vendedor já tinha no ML antes de usar a plataforma (RF11): aparece na busca dos anúncios dele, com
    /// fotos servidas pelo servidor de imagens simulado (<c>mlstatic.com</c>). Cada variação aponta para a foto de mesma posição
    /// (se houver) e só mostra o SKU dela com <c>include_attributes=all</c>, como no ML.
    /// </summary>
    public static string Seed(string sellerId, string title, decimal price, int quantity, string status = "active", string? sku = null,
        string condition = "used", IReadOnlyList<(string Side, int Quantity, string? Sku)>? variations = null, int pictures = 1, string? description = null, string? partNumber = null,
        IReadOnlyList<(string Brand, string Model, int Year, string? Engine)>? vehicles = null, int catalogVehicles = 0, string? shippingDimensions = null,
        IReadOnlyList<(string Id, string Value)>? packageAttributes = null)
    {
        var id = "MLB" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var attributes = new JsonArray(
            new JsonObject { ["id"] = "BRAND", ["value_name"] = "Marca Simulada" },
            new JsonObject { ["id"] = "ITEM_CONDITION", ["value_id"] = condition == "new" ? "2230284" : "2230581", ["value_name"] = condition == "new" ? "Novo" : "Usado" });
        if (sku is not null) attributes.Add(new JsonObject { ["id"] = "SELLER_SKU", ["value_name"] = sku });
        if (partNumber is not null) attributes.Add(new JsonObject { ["id"] = "PART_NUMBER", ["value_name"] = partNumber });
        foreach (var (attribute, value) in packageAttributes ?? []) attributes.Add(new JsonObject { ["id"] = attribute, ["value_name"] = value });
        Items[id] = new JsonObject
        {
            ["id"] = id,
            ["seller_id"] = long.Parse(sellerId, System.Globalization.CultureInfo.InvariantCulture),
            ["title"] = title,
            ["price"] = price,
            ["available_quantity"] = quantity,
            ["status"] = status,
            ["condition"] = condition,
            ["category_id"] = HeadlightCategory,
            ["listing_type_id"] = "gold_special",
            ["attributes"] = attributes,
            ["sale_terms"] = new JsonArray(new JsonObject { ["id"] = "WARRANTY_TYPE", ["value_name"] = "Garantia do vendedor" },
                new JsonObject { ["id"] = "WARRANTY_TIME", ["value_name"] = "90 dias" }),
            ["pictures"] = new JsonArray(Enumerable.Range(1, pictures)
                .Select(n => (JsonNode)new JsonObject { ["id"] = $"SIM-{id}-{n}", ["secure_url"] = pictures > 0 ? $"https://http2.mlstatic.com/D_SIM_{id}_{n}.jpg" : MissingPicture })
                .ToArray()),
            ["variations"] = new JsonArray((variations ?? []).Select((v, n) => (JsonNode)new JsonObject
            {
                ["id"] = 180_000_000_000 + Interlocked.Increment(ref _variations),
                ["attribute_combinations"] = new JsonArray(new JsonObject { ["id"] = "SIDE", ["name"] = "Lado", ["value_id"] = null, ["value_name"] = v.Side }),
                ["price"] = price,
                ["available_quantity"] = v.Quantity,
                ["sold_quantity"] = 0,
                ["picture_ids"] = n < pictures ? new JsonArray($"SIM-{id}-{n + 1}") : new JsonArray(),
                ["seller_custom_field"] = null,
                ["attributes"] = v.Sku is null ? new JsonArray() : new JsonArray(new JsonObject { ["id"] = "SELLER_SKU", ["name"] = "SKU", ["value_name"] = v.Sku }),
            }).ToArray()),
            ["shipping"] = new JsonObject { ["mode"] = "me2", ["dimensions"] = shippingDimensions },
        };
        if (description is not null) Descriptions[id] = description;
        if (vehicles is { Count: > 0 } || catalogVehicles > 0)
            ItemVehicles[id] = ((vehicles ?? []).Select(VehicleProduct).ToList(), catalogVehicles);
        return id;
    }

    /// <summary>Veículos informados pelo vendedor em cada anúncio semeado (ids de produto) e quantos vêm só do catálogo do ML.</summary>
    private static readonly ConcurrentDictionary<string, (List<string> ProductIds, int CatalogOnly)> ItemVehicles = new();

    private static readonly ConcurrentDictionary<string, JsonObject> Products = new();

    /// <summary>Produto do domínio de veículos (como em <c>GET /products/{id}</c>); marca vazia = produto sem atributos.</summary>
    private static string VehicleProduct((string Brand, string Model, int Year, string? Engine) v)
    {
        var id = "MLB-SIM-CAR-" + Interlocked.Increment(ref _products).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var attributes = new JsonArray();
        if (v.Brand.Length > 0)
        {
            attributes.Add(new JsonObject { ["id"] = "BRAND", ["name"] = "Marca", ["value_id"] = "SIM-B", ["value_name"] = v.Brand });
            attributes.Add(new JsonObject { ["id"] = "MODEL", ["name"] = "Modelo", ["value_id"] = "SIM-M", ["value_name"] = v.Model });
            attributes.Add(new JsonObject { ["id"] = "VEHICLE_YEAR", ["name"] = "Ano", ["value_id"] = "SIM-Y", ["value_name"] = v.Year.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            if (v.Engine is not null) attributes.Add(new JsonObject { ["id"] = "ENGINE", ["name"] = "Motor", ["value_id"] = "SIM-E", ["value_name"] = v.Engine });
        }
        Products[id] = new JsonObject
        {
            ["id"] = id, ["status"] = "active", ["domain_id"] = MercadoLivreApiClient.VehicleDomain,
            ["name"] = $"{v.Brand} {v.Model} {v.Year} {v.Engine}".Trim(), ["attributes"] = attributes,
        };
        return id;
    }

    private static int _products;
    private static long _variations;

    private static JsonObject ItemCompatibilities(string itemId)
    {
        var (ids, catalogOnly) = ItemVehicles.TryGetValue(itemId, out var known) ? known : ([], 0);
        var products = new JsonArray(ids.Select(p => (JsonNode)new JsonObject
        {
            ["id"] = "SIM-COMPAT-" + p, ["domain_id"] = MercadoLivreApiClient.VehicleDomain, ["catalog_product_id"] = p,
            ["catalog_product_name"] = (string?)Products[p]["name"], ["source"] = "SELLER",
        }).ToArray());
        if (catalogOnly > 0)
            products.Add(new JsonObject
            {
                ["id"] = null, ["domain_id"] = null, ["catalog_product_id"] = null, ["catalog_product_name"] = "Chevrolet", ["source"] = "CATALOGO",
                ["reputation"] = null, ["total"] = catalogOnly,
            });
        return new JsonObject { ["products"] = products };
    }

    /// <summary>Anúncio sem foto acessível: a foto aponta para um endereço que o servidor de imagens não tem.</summary>
    public static void BreakPictures(string itemId)
    {
        if (Items.TryGetValue(itemId, out var item))
            item["pictures"] = new JsonArray(new JsonObject { ["id"] = "SIM-QUEBRADA", ["secure_url"] = MissingPicture });
    }

    /// <summary>Busca dos anúncios do vendedor no modo scan: o cursor é o deslocamento; página vazia encerra.</summary>
    private static JsonObject SellerItems(string sellerId, string? scrollId, int limit)
    {
        var offset = scrollId is { Length: > 4 } && scrollId.StartsWith("SIM-", StringComparison.Ordinal)
            && int.TryParse(scrollId[4..], System.Globalization.CultureInfo.InvariantCulture, out var o) ? o : 0;
        var ids = Items.Values.Where(i => i["seller_id"]?.ToString() == sellerId && !i.ContainsKey("removed")).Select(i => (string)i["id"]!)
            .Order(StringComparer.Ordinal).Skip(offset).Take(Math.Clamp(limit, 1, 100)).ToList();
        return new JsonObject
        {
            ["seller_id"] = sellerId,
            ["results"] = new JsonArray(ids.Select(i => (JsonNode)JsonValue.Create(i)).ToArray()),
            ["scroll_id"] = $"SIM-{(offset + ids.Count).ToString(System.Globalization.CultureInfo.InvariantCulture)}",
        };
    }

    private static readonly Lazy<byte[]> PictureBytes = new(() =>
    {
        using var gradient = NetVips.Image.Xyz(200, 150)[0].Linear([255.0 / 200], [0.0]).Cast(NetVips.Enums.BandFormat.Uchar);
        using var rgb = gradient.Bandjoin(gradient.Invert(), gradient);
        return rgb.JpegsaveBuffer(q: 85);
    });

    private static HttpResponseMessage Picture(string path) =>
        path.Contains("SEM_FOTO", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PictureBytes.Value) { Headers = { ContentType = new("image/jpeg") } } };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        var segments = path.Trim('/').Split('/');
        var method = request.Method.Method;
        if (request.RequestUri.Host.EndsWith("mlstatic.com", StringComparison.OrdinalIgnoreCase)) return Picture(path);

        return (method, segments) switch
        {
            ("GET", ["users", var seller, "items", "search"]) when query["search_type"] == "scan" =>
                Json(SellerItems(seller, query["scroll_id"], int.TryParse(query["limit"], out var pageSize) ? pageSize : 50)),
            ("GET", ["items", var id, "description"]) => Descriptions.TryGetValue(id, out var text)
                ? Json(new JsonObject { ["text"] = string.Empty, ["plain_text"] = text })
                : NotFound(),
            ("GET", ["users", "me"]) => Json(new JsonObject { ["id"] = 1, ["nickname"] = "VENDEDOR_TESTE", ["tags"] = new JsonArray("normal", "user_product_seller") }),
            ("GET", ["sites", "MLB", "domain_discovery", "search"]) => Json(Predict(query["q"] ?? string.Empty)),
            ("GET", ["categories", var id]) => Categories.TryGetValue(id, out var c) ? Json(Category(id, c.Name, c.Path)) : NotFound(),
            ("GET", ["categories", var id, "attributes"]) => Categories.ContainsKey(id) ? Json(Attributes()) : NotFound(),
            ("POST", ["pictures", "items", "upload"]) => Json(new JsonObject { ["id"] = $"SIM-{Interlocked.Increment(ref _pictures)}-MLB" }),
            ("POST", ["items"]) => Create(await BodyAsync(request, cancellationToken)),
            ("POST", ["items", var id, "description"]) => Describe(id, await BodyAsync(request, cancellationToken)),
            ("PUT", ["items", var id]) => Update(id, await BodyAsync(request, cancellationToken)),
            ("GET", ["items", var id]) => Get(id, query["include_attributes"] == "all"),
            ("POST", ["catalog_domains", MercadoLivreApiClient.VehicleDomain, "attributes", var attribute, "top_values"]) =>
                Json(TopValues(attribute, await BodyAsync(request, cancellationToken))),
            ("POST", ["items", var id, "compatibilities"]) => Compatibilities(id, await BodyAsync(request, cancellationToken)),
            ("GET", ["items", var id, "compatibilities"]) => Items.ContainsKey(id) ? Json(ItemCompatibilities(id)) : NotFound(),
            ("GET", ["products", var id]) => Products.TryGetValue(id, out var product) ? Json((JsonObject)product.DeepClone()) : NotFound(),
            ("GET", ["orders", var id]) => Orders.TryGetValue(id, out var order) ? Json((JsonObject)order.DeepClone()) : NotFound(),
            ("GET", ["shipments", var id]) => request.Headers.Contains("x-format-new") && Shipments.TryGetValue(id, out var shipment)
                ? Json((JsonObject)shipment.DeepClone())
                : NotFound(),
            ("GET", ["questions", var id]) => Questions.TryGetValue(id, out var question) && query["api_version"] == "4"
                ? Json((JsonObject)question.DeepClone())
                : NotFound(),
            ("POST", ["answers"]) => AnswerQuestion(await BodyAsync(request, cancellationToken)),
            ("GET", ["missed_feeds"]) => Json(MissedFeeds(query["topic"] ?? string.Empty, int.TryParse(query["offset"], out var o) ? o : 0,
                int.TryParse(query["limit"], out var l) ? l : 10)),
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

    private static HttpResponseMessage Get(string id, bool allAttributes = false)
    {
        var item = Items.GetOrAdd(id, Unknown);
        if (item.ContainsKey("removed")) return NotFound();
        var copy = (JsonObject)item.DeepClone();
        if (!allAttributes && copy["variations"] is JsonArray variations)
            foreach (var variation in variations.OfType<JsonObject>()) variation.Remove("attributes");
        return Json(copy);
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
