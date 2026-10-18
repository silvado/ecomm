using System.Security.Cryptography;
using System.Text;
using Ecommerce.Application.Channels;
using Ecommerce.Application.Integrations;
using Ecommerce.Application.Tenancy;
using Ecommerce.Application.Vault;
using Ecommerce.Domain.Channels;
using Ecommerce.Domain.Vault;
using Ecommerce.Infrastructure.Integrations;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ecommerce.Infrastructure.Channels.Meta;

/// <summary>
/// Catálogo da Meta da loja do escopo (RF22). Ligar: confere id e token na Graph API, guarda o token e um segredo do feed no
/// cofre e liga a conta no mapa da plataforma. O feed (CA1) é um CSV público num endereço com esse segredo; a atualização
/// rápida (CA2) é o <see cref="MetaCatalogSyncHandler"/>.
/// </summary>
public sealed class MetaCatalogService(
    TenantDbContext db, ISecretVault vault, MetaCatalogClient api, ChannelService channels, PlanDirectory plans, ITenantCatalog tenants,
    IOptions<PlatformOptions> platform, IIntegrationLog log, TimeProvider clock) : IMetaCatalogConnections, IMetaCatalogFeed
{
    public const string TokenSecret = "meta:token";
    public const string FeedSecret = "meta:feed";

    public async Task<MetaCatalogView?> GetAsync(CancellationToken ct = default)
    {
        var connection = await ActiveAsync(ct);
        if (connection is null || await FeedTokenAsync(ct) is not { } feed) return null;
        return new MetaCatalogView(connection.Id, connection.ExternalAccountId, connection.AccountName, FeedUrl(db.RequireTenantId(), feed));
    }

    public async Task<ConnectResult> ConnectAsync(string catalogId, string accessToken, CancellationToken ct = default)
    {
        var id = catalogId?.Trim() ?? string.Empty;
        var token = accessToken?.Trim() ?? string.Empty;
        if (id.Length is 0 or > ChannelConnection.ExternalAccountMaxLength || !id.All(char.IsAsciiDigit))
            return new ConnectResult(ConnectOutcome.Invalid, "Informe o id do catálogo (só números, como aparece no Gerenciador de Comércio).");
        if (token.Length is < 20 or > 1000) return new ConnectResult(ConnectOutcome.Invalid, "Informe o token de acesso do usuário do sistema.");
        var plan = await plans.ForTenantAsync(db.RequireTenantId(), ct);
        if (!plan.Has(Domain.Platform.PlanFeature.ForChannel(SalesChannel.Meta)))
            return new ConnectResult(ConnectOutcome.NotAllowed, PlanDirectory.FeatureMessage(plan, "o canal Instagram/Facebook"));
        if (await ActiveAsync(ct) is { } other && other.ExternalAccountId != id)
            return new ConnectResult(ConnectOutcome.NotAllowed, "A loja já está ligada a outro catálogo. Desconecte-o antes.");

        string? name;
        try
        {
            name = await Logged.RunAsync(log, SalesChannel.Meta, "conferir catálogo", () => api.CatalogNameAsync(token, id, ct), n => n ?? id, null, null, ct);
        }
        catch (ChannelAuthorizationRevokedException e) { return new ConnectResult(ConnectOutcome.Invalid, e.Message); }
        catch (ChannelValidationException) { return new ConnectResult(ConnectOutcome.Invalid, "A Meta não achou este catálogo com este token."); }
        catch (ChannelConnectorException e) { return new ConnectResult(ConnectOutcome.Unavailable, e.Message); }

        ChannelConnection connection;
        try { connection = await channels.ConnectAsync(SalesChannel.Meta, id, ct); }
        catch (InvalidOperationException e) when (e.Message == ChannelService.AccountInUseMessage)
        {
            return new ConnectResult(ConnectOutcome.AccountInUse, "Este catálogo já está ligado a outra loja da plataforma.");
        }
        await vault.SetAsync(SecretKind.ApiKey, TokenSecret, Encoding.UTF8.GetBytes(token), ct: ct);
        if (await FeedTokenAsync(ct) is null)
            await vault.SetAsync(SecretKind.ApiKey, FeedSecret, Encoding.UTF8.GetBytes(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24))), ct: ct);
        connection.Authorized(name, null, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return new ConnectResult(ConnectOutcome.Done, Connection: new ChannelConnectionView(connection.Id, connection.Channel, connection.ExternalAccountId,
            connection.AccountName, connection.Status, connection.StatusReason, connection.CreatedAt, connection.UpdatedAt));
    }

    public async Task<bool> DisconnectAsync(CancellationToken ct = default)
    {
        if (await db.ChannelConnections.FirstOrDefaultAsync(c => c.Channel == SalesChannel.Meta && c.Status != ChannelConnectionStatus.Disabled, ct) is not { } connection)
            return false;
        await vault.DeleteAsync(SecretKind.ApiKey, TokenSecret, ct);
        await vault.DeleteAsync(SecretKind.ApiKey, FeedSecret, ct); // o endereço do feed deixa de valer
        connection.Disconnect(clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await channels.UnmapAccountAsync(connection.Channel, connection.ExternalAccountId, ct);
        return true;
    }

    public async Task<string?> CsvAsync(Guid tenantId, string token, CancellationToken ct = default)
    {
        if (await FeedTokenAsync(ct) is not { } expected
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(token ?? string.Empty)))
            return null;
        var (origin, name) = await StoreAsync(tenantId, ct);
        return MetaCatalogItems.Csv(await MetaCatalogItems.LoadAsync(db, origin, name, null, ct));
    }

    /// <summary>Endereço da loja virtual (subdomínio da plataforma) e nome, para links e marca dos itens.</summary>
    public async Task<(string Origin, string Name)> StoreAsync(Guid tenantId, CancellationToken ct)
    {
        var store = await tenants.FindByIdAsync(tenantId, ct) ?? throw new InvalidOperationException("Loja não encontrada.");
        var domain = platform.Value.Domain is { Length: > 0 } d ? d : "localhost";
        return ($"https://{store.Slug}.{domain}", store.TradeName);
    }

    private string FeedUrl(Guid tenantId, string feed)
    {
        var domain = platform.Value.Domain is { Length: > 0 } d ? d : "localhost";
        return $"https://api.{domain}/api/catalogo/{tenantId}/{feed}.csv";
    }

    private Task<ChannelConnection?> ActiveAsync(CancellationToken ct) =>
        db.ChannelConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Channel == SalesChannel.Meta && c.Status == ChannelConnectionStatus.Active, ct);

    private async Task<string?> FeedTokenAsync(CancellationToken ct) =>
        await vault.GetAsync(SecretKind.ApiKey, FeedSecret, ct) is { Length: > 0 } bytes ? Encoding.UTF8.GetString(bytes) : null;
}

/// <summary>
/// RF22 CA2: a cada mudança de estoque ou preço, atualiza o item no catálogo da Meta pela Catalog Batch API (em segundos;
/// o feed agendado é no máximo de hora em hora). Token recusado: a conexão fica "autorizar de novo". Meta fora do ar: a
/// fila tenta de novo (RF28).
/// </summary>
public static class MetaCatalogSyncHandler
{
    // O serviço do catálogo também lê o banco da plataforma (plano, loja): a transação é a do banco das lojas.
    [Wolverine.Attributes.Transactional(typeof(TenantDbContext))]
    public static async Task Handle(SyncMetaCatalogItem command, TenantDbContext db, ISecretVault vault, MetaCatalogClient api, MetaCatalogService service,
        IIntegrationLog log, TimeProvider clock, CancellationToken ct)
    {
        var connection = await db.ChannelConnections.FirstOrDefaultAsync(c => c.Channel == SalesChannel.Meta && c.Status == ChannelConnectionStatus.Active, ct);
        if (connection is null) return;
        if (await vault.GetAsync(SecretKind.ApiKey, MetaCatalogService.TokenSecret, ct) is not { Length: > 0 } tokenBytes)
        {
            connection.RequireReauthorization("O token da Meta não foi encontrado. Informe-o de novo em Canais de venda.", clock.GetUtcNow());
            return;
        }
        var (origin, name) = await service.StoreAsync(db.RequireTenantId(), ct);
        if ((await MetaCatalogItems.LoadAsync(db, origin, name, [command.PartId], ct)).SingleOrDefault() is not { } item) return; // sem foto: fora do catálogo
        try
        {
            _ = await Logged.RunAsync(log, SalesChannel.Meta, "atualizar catálogo",
                () => api.UpsertAsync(Encoding.UTF8.GetString(tokenBytes), connection.ExternalAccountId, [item.ToJson()], ct),
                w => w.Count == 0 ? $"{item.Availability} · {item.Quantity} un." : string.Join(" ", w), null, command.PartId, ct);
        }
        catch (ChannelAuthorizationRevokedException e)
        {
            connection.RequireReauthorization(e.Message, clock.GetUtcNow());
        }
    }
}
