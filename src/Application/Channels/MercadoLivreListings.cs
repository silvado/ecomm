namespace Ecommerce.Application.Channels;

/// <summary>Categoria do ML sugerida pelo preditor a partir do título da peça (a primeira é a mais provável).</summary>
public sealed record CategoryOption(string CategoryId, string CategoryName, string? DomainName);

/// <summary>Valor permitido de um atributo de lista.</summary>
public sealed record AttributeValueOption(string Id, string Name);

/// <summary>
/// Atributo de uma categoria do ML, como o formulário precisa (já sem os que a loja não pode preencher: read_only, fixed,
/// inferred). <see cref="Required"/>: obrigatório para publicar (tag <c>required</c>, ou <c>new_required</c> em peça nova).
/// <see cref="ValueType"/>: <c>list</c>, <c>string</c>, <c>number</c>, <c>number_unit</c> ou <c>boolean</c>.
/// </summary>
public sealed record CategoryAttributeSpec(
    string Id, string Name, string ValueType, bool Required, bool ConditionalRequired, bool Multivalued, int? MaxLength,
    IReadOnlyList<AttributeValueOption> Values, IReadOnlyList<string> Units, string? DefaultUnit);

/// <summary>Detalhe da categoria para publicar: limite do título (family_name) e de fotos e se aceita anúncio.</summary>
public sealed record CategoryDetail(string CategoryId, string Name, string Path, int MaxTitleLength, int MaxPictures, bool ListingAllowed);

/// <summary>Valor escolhido para um atributo: <see cref="ValueId"/> (lista) ou <see cref="ValueName"/> (texto, número com unidade).</summary>
public sealed record AttributeValue(string Id, string? ValueId, string? ValueName);

/// <summary>
/// Opções do anúncio no ML (guardadas no anúncio para as próximas edições). <see cref="ListingTypeId"/>: <c>gold_special</c>
/// (Clássico) ou <c>gold_pro</c> (Premium). Garantia: <see cref="WarrantyType"/> = valor de WARRANTY_TYPE (ex.: "Garantia do
/// vendedor") ou nulo para "Sem garantia"; <see cref="WarrantyTime"/> ex.: "90 dias".
/// </summary>
public sealed record MercadoLivreListingOptions(
    string CategoryId, string ListingTypeId, IReadOnlyList<AttributeValue> Attributes, string? WarrantyType, string? WarrantyTime);

/// <summary>Formulário de publicação de uma peça no ML: categoria, atributos (já com o que dá para pré-preencher) e limites.</summary>
public sealed record MercadoLivrePublishForm(
    CategoryDetail Category, IReadOnlyList<CategoryAttributeSpec> Attributes, IReadOnlyList<AttributeValue> Prefilled, IReadOnlyList<string> Warnings);

public enum PublishOutcome
{
    Done,
    /// <summary>Peça, conexão ou dados do formulário não permitem publicar (mensagem pronta para o usuário).</summary>
    Invalid,
    /// <summary>O ML recusou o anúncio (validação dele): mensagem com o motivo.</summary>
    Refused,
    /// <summary>O ML não respondeu: tentar de novo.</summary>
    Unavailable,
}

/// <param name="Compatibilities">Resultado do envio das compatibilidades logo depois de publicar (nulo se não houve envio).</param>
public sealed record PublishResult(PublishOutcome Outcome, string? Message = null, Guid? ListingId = null, CompatibilityReport? Compatibilities = null);

/// <summary>
/// Compatibilidades da peça enviadas ao anúncio do ML (RF21 CA3): <see cref="Sent"/> famílias aceitas (marca, modelo, ano e,
/// quando preciso, motor), <see cref="Unmapped"/> veículos que não foram achados no catálogo de veículos do ML (a loja
/// inclui pelo gerenciador de compatibilidades do ML) e <see cref="Error"/> quando o ML recusou ou não respondeu.
/// </summary>
public sealed record CompatibilityReport(int Sent, IReadOnlyList<string> Unmapped, string? Error = null);

/// <summary>Envia ao anúncio do ML as compatibilidades cadastradas na peça (loja do escopo).</summary>
public interface IMercadoLivreCompatibilities
{
    /// <exception cref="InvalidOperationException">Anúncio não é do ML, não está publicado ou a conexão não está ativa.</exception>
    Task<CompatibilityReport> SyncAsync(Guid listingId, CancellationToken ct = default);
}

/// <summary>
/// Publicar uma peça no Mercado Livre (RF21 CA3), na loja do escopo e na conta do ML ligada a ela. As consultas (preditor,
/// categoria, atributos) usam o token do vendedor.
/// </summary>
public interface IMercadoLivrePublishing
{
    /// <summary>Categorias sugeridas pelo título da peça (vazio se o preditor não achar nada).</summary>
    Task<IReadOnlyList<CategoryOption>> SuggestCategoriesAsync(Guid partId, CancellationToken ct = default);

    /// <exception cref="ArgumentException">Categoria inexistente ou que não aceita anúncios (mensagem ao usuário).</exception>
    Task<MercadoLivrePublishForm> FormAsync(Guid partId, string categoryId, CancellationToken ct = default);

    Task<PublishResult> PublishAsync(Guid partId, MercadoLivreListingOptions options, CancellationToken ct = default);
}

/// <summary>O ML recusou o pedido por validação (400): <see cref="Exception.Message"/> traz os motivos dele, para a loja corrigir.</summary>
public sealed class ChannelValidationException(string message) : Exception(message);
