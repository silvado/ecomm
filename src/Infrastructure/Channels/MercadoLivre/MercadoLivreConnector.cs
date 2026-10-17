using System.Text.Json;
using System.Text.Json.Nodes;
using Ecommerce.Application.Channels;
using Ecommerce.Application.Storage;
using Ecommerce.Domain.Catalog;
using Ecommerce.Domain.Channels;
using Ecommerce.Infrastructure.Catalog;

namespace Ecommerce.Infrastructure.Channels.MercadoLivre;

/// <summary>
/// Conector do Mercado Livre de uma conexão da loja (RF21 CA3, RF24). Publica com fotos (enviadas ao ML em JPG), atributos,
/// tipo, garantia e descrição; atualiza preço e quantidade; pausa e reativa pelo <c>status</c> (zerar a quantidade só pausa
/// item novo, e peça usada é a regra aqui); lê o estado para a reconciliação. Fontes em <see cref="MercadoLivreApiClient"/>.
/// </summary>
public sealed class MercadoLivreConnector(ChannelConnection connection, MercadoLivreApiClient api, MercadoLivreCredentials credentials, IFileStorage storage)
    : IChannelConnector
{
    public const string Currency = "BRL";
    public const string SkuAttribute = "SELLER_SKU";

    public string Channel => SalesChannel.MercadoLivre;

    public async Task<string> PublishAsync(ListingDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var options = draft.MercadoLivre ?? throw new ArgumentException("Escolha a categoria e os atributos do anúncio.", nameof(draft));
        var token = await credentials.AccessTokenAsync(connection.Id, ct);
        var category = await api.CategoryAsync(token, options.CategoryId, ct)
            ?? throw new ChannelValidationException("A categoria escolhida não existe mais no Mercado Livre.");

        var pictures = new List<string>();
        foreach (var photoId in (draft.PhotoIds ?? []).Take(category.MaxPictures))
        {
            if (await storage.GetAsync(StorageArea.Private, PartPhoto.OriginalKey(connection.TenantId, draft.PartId, photoId), ct) is not { } file) continue;
            using var buffer = new MemoryStream();
            await using (file.Content) await file.Content.CopyToAsync(buffer, ct);
            var (jpeg, error) = await PhotoProcessor.ForChannelAsync(buffer.ToArray(), ct);
            if (jpeg is null) throw new ChannelValidationException($"Uma foto da peça não pôde ser preparada: {error}");
            pictures.Add(await api.UploadPictureAsync(token, jpeg, ct));
        }
        if (pictures.Count == 0) throw new ChannelValidationException("O Mercado Livre exige ao menos uma foto.");

        var item = BuildItem(draft, options, category.MaxTitleLength, await api.IsUserProductSellerAsync(token, ct), pictures);
        var itemId = await api.CreateItemAsync(token, item, ct);
        // A descrição vai num recurso próprio, depois de criado o anúncio. Falhar aqui não desfaz o anúncio.
        if (!string.IsNullOrWhiteSpace(draft.Description))
        {
            try { await api.SetDescriptionAsync(token, itemId, draft.Description, ct); }
            catch (Exception e) when (e is ChannelValidationException or ChannelConnectorException) { /* anúncio criado sem descrição; a loja edita lá */ }
        }
        return itemId;
    }

    public async Task UpdatePriceAndQuantityAsync(string externalId, decimal price, int quantity, CancellationToken ct = default)
    {
        var warnings = await api.UpdateItemAsync(await credentials.AccessTokenAsync(connection.Id, ct), externalId,
            new JsonObject { ["price"] = price, ["available_quantity"] = quantity }, ct);
        // Automação de preços ligada no ML: o preço é ignorado (https://developers.mercadolivre.com.br/pt_br/automatizacoes-de-precos).
        if (warnings.Count > 0) throw new ChannelValidationException("O Mercado Livre não atualizou o preço: " + string.Join(" ", warnings));
    }

    public async Task PauseAsync(string externalId, CancellationToken ct = default) =>
        await api.UpdateItemAsync(await credentials.AccessTokenAsync(connection.Id, ct), externalId, new JsonObject { ["status"] = "paused" }, ct);

    public async Task ResumeAsync(string externalId, int quantity, CancellationToken ct = default)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity), "Reativar exige quantidade maior que zero.");
        var token = await credentials.AccessTokenAsync(connection.Id, ct);
        await api.UpdateItemAsync(token, externalId, new JsonObject { ["available_quantity"] = quantity }, ct);
        await api.UpdateItemAsync(token, externalId, new JsonObject { ["status"] = "active" }, ct);
    }

    public async Task<RemoteListing?> GetRemoteStateAsync(string externalId, CancellationToken ct = default)
    {
        if (await api.GetItemAsync(await credentials.AccessTokenAsync(connection.Id, ct), externalId, ct) is not { } item) return null;
        var status = MercadoLivreApiClient.Text(item, "status") switch
        {
            "active" => RemoteListingStatus.Active,
            "closed" => RemoteListingStatus.Closed,
            // paused, under_review, inactive, payment_required: não está vendendo.
            _ => RemoteListingStatus.Paused,
        };
        var quantity = item.TryGetProperty("available_quantity", out var q) && q.TryGetInt32(out var qi) ? qi : 0;
        var price = item.TryGetProperty("price", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDecimal() : 0m;
        return new RemoteListing(externalId, status, quantity, price);
    }

    /// <summary>
    /// Corpo do <c>POST /items</c>. Vendedor no modelo User Products: <c>family_name</c> e nada de <c>title</c> (o ML monta o
    /// título); senão, <c>title</c>. A condição vai no atributo ITEM_CONDITION (o campo <c>condition</c> segue por
    /// compatibilidade, só para nova/usada). Sem garantia escolhida: "Sem garantia".
    /// </summary>
    public static JsonObject BuildItem(ListingDraft draft, MercadoLivreListingOptions options, int maxTitleLength, bool userProductSeller, IReadOnlyList<string> pictureIds)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pictureIds);
        var name = draft.Title.Trim();
        if (name.Length > maxTitleLength) name = name[..maxTitleLength].TrimEnd();

        var attributes = new JsonArray();
        foreach (var a in options.Attributes.Where(a => !string.IsNullOrWhiteSpace(a.Id) && (!string.IsNullOrWhiteSpace(a.ValueId) || !string.IsNullOrWhiteSpace(a.ValueName))))
            attributes.Add(string.IsNullOrWhiteSpace(a.ValueId)
                ? new JsonObject { ["id"] = a.Id, ["value_name"] = a.ValueName!.Trim() }
                : new JsonObject { ["id"] = a.Id, ["value_id"] = a.ValueId });
        if (options.Attributes.All(a => a.Id != SkuAttribute)) attributes.Add(new JsonObject { ["id"] = SkuAttribute, ["value_name"] = draft.InternalCode });

        var saleTerms = string.IsNullOrWhiteSpace(options.WarrantyType)
            ? new JsonArray(new JsonObject { ["id"] = "WARRANTY_TYPE", ["value_name"] = "Sem garantia" })
            : new JsonArray(new JsonObject { ["id"] = "WARRANTY_TYPE", ["value_name"] = options.WarrantyType },
                new JsonObject { ["id"] = "WARRANTY_TIME", ["value_name"] = options.WarrantyTime ?? "90 dias" });

        var item = new JsonObject
        {
            [userProductSeller ? "family_name" : "title"] = name,
            ["category_id"] = options.CategoryId,
            ["price"] = draft.Price,
            ["currency_id"] = Currency,
            ["available_quantity"] = draft.Quantity,
            ["buying_mode"] = "buy_it_now",
            ["listing_type_id"] = options.ListingTypeId,
            ["sale_terms"] = saleTerms,
            ["pictures"] = new JsonArray(pictureIds.Select(id => (JsonNode)new JsonObject { ["id"] = id }).ToArray()),
            ["attributes"] = attributes,
        };
        if (draft.Condition is PartCondition.New or PartCondition.Used) item["condition"] = draft.Condition == PartCondition.New ? "new" : "used";
        return item;
    }

    /// <summary>Opções guardadas no anúncio (JSON), para editar e republicar.</summary>
    public static string Serialize(MercadoLivreListingOptions options) => JsonSerializer.Serialize(options, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
