using Ecommerce.Domain.Channels;

namespace Ecommerce.Application.Channels;

/// <summary>
/// Peça como o canal precisa para anunciar. <see cref="PhotoIds"/>: fotos da peça na ordem (a primeira é a capa; o conector
/// lê o original no storage). <see cref="MercadoLivre"/>: categoria, atributos, tipo e garantia escolhidos no painel (RF21 CA3).
/// </summary>
public sealed record ListingDraft(Guid PartId, string InternalCode, string Title, string Description, decimal Price, int Quantity,
    Domain.Catalog.PartCondition Condition = Domain.Catalog.PartCondition.Used, IReadOnlyList<Guid>? PhotoIds = null,
    MercadoLivreListingOptions? MercadoLivre = null);

public enum RemoteListingStatus
{
    Active,
    Paused,
    Closed,
}

/// <summary>Estado do anúncio no canal, para a reconciliação (RF27).</summary>
public sealed record RemoteListing(string ExternalId, RemoteListingStatus Status, int Quantity, decimal Price);

/// <summary>Falha de comunicação com o canal (fora do ar, limite de taxa): a fila tenta de novo (RF28).</summary>
public sealed class ChannelConnectorException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Operações comuns a todos os canais (requisitos §5): publicar, atualizar preço e quantidade, pausar, retomar e ler o
/// estado remoto. Os avisos do canal entram pelo inbox (IChannelEventProcessor). Cada implementação passa pelos testes
/// de contrato genéricos (ChannelConnectorContract).
/// </summary>
public interface IChannelConnector
{
    string Channel { get; }

    /// <returns>Id do anúncio no canal.</returns>
    Task<string> PublishAsync(ListingDraft draft, CancellationToken ct = default);

    Task UpdatePriceAndQuantityAsync(string externalId, decimal price, int quantity, CancellationToken ct = default);

    Task PauseAsync(string externalId, CancellationToken ct = default);

    /// <summary>Reativa com a quantidade atual (alguns canais exigem quantidade &gt; 0 para reativar).</summary>
    Task ResumeAsync(string externalId, int quantity, CancellationToken ct = default);

    /// <summary>Nulo se o anúncio não existe mais no canal.</summary>
    Task<RemoteListing?> GetRemoteStateAsync(string externalId, CancellationToken ct = default);
}

/// <summary>Conector da conexão (com as credenciais dela, no cofre da loja do escopo).</summary>
public interface IChannelConnectorResolver
{
    Task<IChannelConnector?> ResolveAsync(ChannelConnection connection, CancellationToken ct = default);
}

/// <summary>Uma por anúncio a cada mudança de estoque: falha num canal tenta de novo só nele.</summary>
/// <param name="ChangedAt">Quando o estoque mudou, para a métrica de latência (RF24: até 60 s).</param>
public sealed record SyncListing(Guid ListingId, DateTimeOffset? ChangedAt);

/// <summary>A loja foi suspensa ou reativada (RF38): reconfere todos os anúncios (pausa ou retoma nos canais).</summary>
public sealed record ResyncStoreListings;

/// <summary>Reconciliação de uma loja (RF27): agenda a conferência de cada anúncio, espaçada para respeitar os limites do canal.</summary>
public sealed record ReconcileChannels;

/// <summary>Confere um anúncio com o canal e corrige o canal se divergir (o estoque local é a verdade).</summary>
public sealed record ReconcileListing(Guid ListingId);

public enum ListingCommandOutcome
{
    Done,
    NotFound,
    NotAllowed,
}

public sealed record ListingCommandResult(ListingCommandOutcome Outcome, string? Message = null);

/// <summary>Lojista pausa ou retoma o anúncio pelo painel.</summary>
public sealed record ChangeListingByUser(Guid ListingId, bool Pause);

public sealed record ListingView(Guid Id, string Channel, string ExternalAccount, string? ExternalId, ListingStatus Status, int? LastPushedQuantity,
    decimal? LastPushedPrice, DateTimeOffset? LastSyncedAt, string? LastError, DateTimeOffset? LastReconciledAt = null);

public interface IChannelQueries
{
    Task<IReadOnlyList<ListingView>> ListingsForPartAsync(Guid partId, CancellationToken ct = default);
}
