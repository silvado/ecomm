using Ecommerce.Application.Channels;
using Ecommerce.Domain.Catalog;
using Ecommerce.Domain.Channels;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Channels.MercadoLivre;

/// <summary>
/// Publicar peça no ML (RF21 CA3) na loja do escopo: sugere a categoria pelo título, monta o formulário com os atributos
/// da categoria (pré-preenche condição, marca vazia, número de peça pelo primeiro OEM e o SKU pelo código interno), confere
/// os obrigatórios, publica pela conexão ativa da loja e, em seguida, envia as compatibilidades da peça.
/// </summary>
public sealed class MercadoLivrePublishingService(
    TenantDbContext db, ChannelService channels, MercadoLivreApiClient api, MercadoLivreCredentials credentials, IMercadoLivreCompatibilities compatibilities)
    : IMercadoLivrePublishing
{
    public static readonly IReadOnlySet<string> ListingTypes = new HashSet<string>(StringComparer.Ordinal) { "gold_special", "gold_pro" };

    /// <summary>Valores de ITEM_CONDITION na documentação (https://developers.mercadolivre.com.br/pt_br/publicacao-de-produtos).</summary>
    private static readonly Dictionary<PartCondition, string> ConditionNames = new()
    {
        [PartCondition.New] = "Novo",
        [PartCondition.Used] = "Usado",
        [PartCondition.Refurbished] = "Recondicionado",
    };

    public async Task<IReadOnlyList<CategoryOption>> SuggestCategoriesAsync(Guid partId, CancellationToken ct = default)
    {
        var (connection, part) = await ContextAsync(partId, ct);
        return await api.PredictAsync(await credentials.AccessTokenAsync(connection.Id, ct), part.Title, ct);
    }

    public async Task<MercadoLivrePublishForm> FormAsync(Guid partId, string categoryId, CancellationToken ct = default)
    {
        var (connection, part) = await ContextAsync(partId, ct);
        var token = await credentials.AccessTokenAsync(connection.Id, ct);
        var category = await api.CategoryAsync(token, categoryId, ct) ?? throw new ArgumentException("Categoria não encontrada no Mercado Livre.", nameof(categoryId));
        if (!category.ListingAllowed) throw new ArgumentException("Esta categoria não aceita anúncios. Escolha uma subcategoria mais específica.", nameof(categoryId));
        var attributes = await api.AttributesAsync(token, categoryId, part.Condition == PartCondition.New, ct);

        var prefilled = new List<AttributeValue>();
        if (attributes.FirstOrDefault(a => a.Id == "ITEM_CONDITION") is { } condition
            && condition.Values.FirstOrDefault(v => v.Name.Equals(ConditionNames[part.Condition], StringComparison.OrdinalIgnoreCase)) is { } value)
            prefilled.Add(new AttributeValue(condition.Id, value.Id, null));
        if (part.OemCodes.Count > 0 && part.OemCodes[0] is { } oem && attributes.FirstOrDefault(a => a.Id is "PART_NUMBER" or "OEM") is { } number)
            prefilled.Add(new AttributeValue(number.Id, null, oem));
        if (attributes.Any(a => a.Id == MercadoLivreConnector.SkuAttribute))
            prefilled.Add(new AttributeValue(MercadoLivreConnector.SkuAttribute, null, part.InternalCode));

        var warnings = new List<string>();
        if (part.Title.Length > category.MaxTitleLength)
            warnings.Add($"O título tem {part.Title.Length} caracteres; o Mercado Livre aceita {category.MaxTitleLength} nesta categoria e o restante será cortado.");
        if (part.Photos > category.MaxPictures)
            warnings.Add($"A categoria aceita {category.MaxPictures} fotos: vão as {category.MaxPictures} primeiras.");
        if (!part.HasDimensions)
            warnings.Add("A peça está sem as medidas da embalagem: o frete do Mercado Livre pode ficar errado. Preencha em Embalagem.");
        return new MercadoLivrePublishForm(category, attributes, prefilled, warnings);
    }

    public async Task<PublishResult> PublishAsync(Guid partId, MercadoLivreListingOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!ListingTypes.Contains(options.ListingTypeId)) return Invalid("Tipo de anúncio inválido: escolha Clássico ou Premium.");
        if (string.IsNullOrWhiteSpace(options.CategoryId)) return Invalid("Escolha a categoria do anúncio.");
        if (!string.IsNullOrWhiteSpace(options.WarrantyType) && string.IsNullOrWhiteSpace(options.WarrantyTime))
            return Invalid("Informe o tempo de garantia (ex.: 90 dias).");

        ChannelConnection connection;
        PartFacts part;
        try { (connection, part) = await ContextAsync(partId, ct); }
        catch (InvalidOperationException e) { return Invalid(e.Message); }
        if (part.Available <= 0) return Invalid("A peça está sem saldo: o Mercado Livre não aceita anúncio de peça usada com quantidade zero.");
        if (part.Condition == PartCondition.Refurbished && string.IsNullOrWhiteSpace(options.WarrantyType))
            return Invalid("Peça recondicionada exige garantia no Mercado Livre (90 dias ou mais).");

        try
        {
            var token = await credentials.AccessTokenAsync(connection.Id, ct);
            var attributes = await api.AttributesAsync(token, options.CategoryId, part.Condition == PartCondition.New, ct);
            var filled = options.Attributes.Where(a => !string.IsNullOrWhiteSpace(a.ValueId) || !string.IsNullOrWhiteSpace(a.ValueName)).Select(a => a.Id).ToHashSet();
            var missing = attributes.Where(a => a.Required && !filled.Contains(a.Id)).Select(a => a.Name).ToList();
            if (missing.Count > 0) return Invalid($"Preencha os campos obrigatórios do Mercado Livre: {string.Join(", ", missing)}.");
            var known = attributes.Select(a => a.Id).Append(MercadoLivreConnector.SkuAttribute).ToHashSet();
            var cleaned = options with { Attributes = options.Attributes.Where(a => known.Contains(a.Id)).ToList() };

            var listing = await channels.PublishAsync(connection.Id, partId, cleaned, ct);
            // Compatibilidades logo em seguida: falhar aqui não desfaz o anúncio (dá para reenviar pelo anúncio).
            CompatibilityReport report;
            try { report = await compatibilities.SyncAsync(listing.Id, ct); }
            catch (Exception e) when (e is ChannelConnectorException or ChannelAuthorizationRevokedException or InvalidOperationException)
            {
                report = new CompatibilityReport(0, [], $"As compatibilidades não foram enviadas: {e.Message}");
            }
            return new PublishResult(PublishOutcome.Done, ListingId: listing.Id, Compatibilities: report);
        }
        catch (ChannelValidationException e)
        {
            return new PublishResult(PublishOutcome.Refused, $"O Mercado Livre recusou o anúncio: {e.Message}");
        }
        catch (ChannelAuthorizationRevokedException e)
        {
            return Invalid(e.Message);
        }
        catch (ChannelConnectorException e)
        {
            return new PublishResult(PublishOutcome.Unavailable, e.Message);
        }
        catch (InvalidOperationException e)
        {
            return Invalid(e.Message);
        }
    }

    private sealed record PartFacts(string Title, string InternalCode, PartCondition Condition, IReadOnlyList<string> OemCodes, int Photos, bool HasDimensions, int Available);

    /// <exception cref="InvalidOperationException">Sem conexão ativa do ML ou peça inexistente (mensagem ao usuário).</exception>
    private async Task<(ChannelConnection Connection, PartFacts Part)> ContextAsync(Guid partId, CancellationToken ct)
    {
        var connection = await db.ChannelConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Channel == SalesChannel.MercadoLivre && c.Status == ChannelConnectionStatus.Active, ct)
            ?? throw new InvalidOperationException("Conecte a conta do Mercado Livre em Canais de venda antes de anunciar.");
        var part = await db.Parts.AsNoTracking().Include(p => p.OemCodes).Include(p => p.Photos).SingleOrDefaultAsync(p => p.Id == partId, ct)
            ?? throw new InvalidOperationException("Peça não encontrada.");
        var available = await db.Stocks.Where(s => s.PartId == partId).Select(s => s.OnHand - s.Reserved).SingleOrDefaultAsync(ct);
        return (connection, new PartFacts(part.Title, part.InternalCode, part.Condition, part.OemCodes.Select(o => o.Code).ToList(), part.Photos.Count,
            part.HasShippingDimensions, available));
    }

    private static PublishResult Invalid(string message) => new(PublishOutcome.Invalid, message);
}
