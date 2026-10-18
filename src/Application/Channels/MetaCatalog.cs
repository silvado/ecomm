namespace Ecommerce.Application.Channels;

/// <summary>Catálogo da Meta ligado à loja: nome, id e o endereço do feed para cadastrar no Commerce Manager (RF22 CA1).</summary>
public sealed record MetaCatalogView(Guid ConnectionId, string CatalogId, string? CatalogName, string FeedUrl);

/// <summary>
/// Catálogo do Instagram/Facebook (RF22) na loja do escopo. A loja informa o id do catálogo e um token de acesso (usuário
/// do sistema do Gerenciador de Negócios, com permissão no catálogo) — como as chaves dos gateways. O token vai para o cofre.
/// </summary>
public interface IMetaCatalogConnections
{
    Task<MetaCatalogView?> GetAsync(CancellationToken ct = default);

    Task<ConnectResult> ConnectAsync(string catalogId, string accessToken, CancellationToken ct = default);

    Task<bool> DisconnectAsync(CancellationToken ct = default);
}

/// <summary>Feed do catálogo (CSV no formato da Meta), pelo endereço com token: nulo se a loja ou o token não conferem.</summary>
public interface IMetaCatalogFeed
{
    Task<string?> CsvAsync(Guid tenantId, string token, CancellationToken ct = default);
}

/// <summary>
/// Atualiza um item do catálogo da Meta na hora (Catalog Batch API, RF22 CA2): disponibilidade, preço e quantidade lidos
/// agora. Publicada a cada <see cref="Inventory.StockChanged"/> de loja com o catálogo ligado.
/// </summary>
public sealed record SyncMetaCatalogItem(Guid PartId);
