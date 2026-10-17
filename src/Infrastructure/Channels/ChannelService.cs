using Ecommerce.Application.Channels;
using Ecommerce.Application.Integrations;
using Ecommerce.Domain.Channels;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ecommerce.Infrastructure.Channels;

/// <summary>
/// Conexões e anúncios da loja do escopo (E2). Conectar grava a conta também no mapa da plataforma (roteamento do inbox,
/// RF26): uma conta externa pertence a uma loja só. A conexão real (OAuth do ML) e a publicação com categoria e atributos
/// entram no RF21; por ora só o canal simulado usa conectar e publicar.
/// </summary>
public sealed class ChannelService(TenantDbContext db, PlatformDbContext platform, IChannelConnectorResolver connectors, IIntegrationLog log,
    PlanDirectory plans, TimeProvider clock) : IChannelQueries
{
    public async Task<IReadOnlyList<ListingView>> ListingsForPartAsync(Guid partId, CancellationToken ct = default) =>
        await (from l in db.ChannelListings.AsNoTracking()
               join c in db.ChannelConnections.AsNoTracking() on l.ConnectionId equals c.Id
               where l.PartId == partId
               orderby c.Channel
               select new ListingView(l.Id, c.Channel, c.ExternalAccountId, l.ExternalId, l.Status, l.LastPushedQuantity, l.LastPushedPrice,
                   l.LastSyncedAt, l.LastError, l.LastReconciledAt)).ToListAsync(ct);

    /// <summary>Conexão da loja com o canal simulado, se houver (dev e testes).</summary>
    public Task<Guid?> FakeConnectionIdAsync(CancellationToken ct = default) =>
        db.ChannelConnections.Where(c => c.Channel == Integrations.FakeChannelProcessor.ChannelName).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);

    /// <exception cref="InvalidOperationException">Conta já ligada a outra loja.</exception>
    public async Task<ChannelConnection> ConnectAsync(string channel, string externalAccountId, CancellationToken ct = default)
    {
        var tenantId = db.RequireTenantId();
        var existing = await db.ChannelConnections.SingleOrDefaultAsync(c => c.Channel == channel && c.ExternalAccountId == externalAccountId, ct);
        if (existing is not null)
        {
            // Desconectada antes: o mapa da plataforma saiu junto e volta agora (se outra loja não pegou a conta).
            if (!await platform.ChannelAccounts.AnyAsync(a => a.Channel == channel && a.ExternalAccountId == externalAccountId, ct))
                await MapAccountAsync(new ChannelAccount(tenantId, channel, externalAccountId, clock.GetUtcNow()), ct);
            return existing;
        }
        // RF37: o canal precisa estar no plano (o simulado, só de dev, não entra na conta).
        if (channel != Integrations.FakeChannelProcessor.ChannelName)
        {
            var plan = await plans.ForTenantAsync(tenantId, ct);
            if (!plan.Has(PlanFeature.ForChannel(channel))) throw new InvalidOperationException(PlanDirectory.FeatureMessage(plan, $"o canal {channel}"));
        }

        var now = clock.GetUtcNow();
        var account = new ChannelAccount(tenantId, channel, externalAccountId, now);
        await MapAccountAsync(account, ct);

        var connection = new ChannelConnection(tenantId, channel, externalAccountId, now);
        db.ChannelConnections.Add(connection);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Bancos diferentes: desfaz o mapa se a conexão não gravou.
            platform.ChannelAccounts.Remove(account);
            await platform.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        return connection;
    }

    /// <summary>Tira a conta do mapa da plataforma (loja desconectou): os avisos do canal deixam de vir para a loja.</summary>
    public async Task UnmapAccountAsync(string channel, string externalAccountId, CancellationToken ct = default)
    {
        var tenantId = db.RequireTenantId();
        await platform.ChannelAccounts.Where(a => a.TenantId == tenantId && a.Channel == channel && a.ExternalAccountId == externalAccountId)
            .ExecuteDeleteAsync(ct);
    }

    private async Task MapAccountAsync(ChannelAccount account, CancellationToken ct)
    {
        platform.ChannelAccounts.Add(account);
        try
        {
            await platform.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            platform.Entry(account).State = EntityState.Detached;
            throw new InvalidOperationException(AccountInUseMessage, e);
        }
    }

    public const string AccountInUseMessage = "Esta conta do canal já está ligada a outra loja.";

    /// <summary>Publica a peça na conexão (cria o anúncio no canal com saldo e preço atuais).</summary>
    /// <param name="mercadoLivre">Categoria, atributos, tipo e garantia (obrigatório no ML; guardado no anúncio).</param>
    /// <exception cref="InvalidOperationException">Peça não pode ser anunciada ou o canal não está disponível.</exception>
    public async Task<ChannelListing> PublishAsync(Guid connectionId, Guid partId, MercadoLivreListingOptions? mercadoLivre = null, CancellationToken ct = default)
    {
        var connection = await db.ChannelConnections.SingleOrDefaultAsync(c => c.Id == connectionId, ct)
            ?? throw new InvalidOperationException("Conexão não encontrada.");
        if (connection.Status != ChannelConnectionStatus.Active) throw new InvalidOperationException("A conexão com o canal não está ativa. Confira em Canais de venda.");
        var part = await (from p in db.Parts
                          join s in db.Stocks on p.Id equals s.PartId
                          where p.Id == partId
                          select new { p.Id, p.InternalCode, p.Title, p.Description, p.Price, p.Status, p.Condition, Available = s.OnHand - s.Reserved }).SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Peça não encontrada.");
        if (part.Status != Domain.Catalog.PartStatus.Active) throw new InvalidOperationException("Só peça ativa pode ser anunciada.");
        if (await db.ChannelListings.AnyAsync(l => l.PartId == partId && l.ConnectionId == connectionId, ct))
            throw new InvalidOperationException("A peça já está anunciada neste canal.");
        var connector = await connectors.ResolveAsync(connection, ct) ?? throw new InvalidOperationException("Canal sem conector disponível.");
        var photos = await db.PartPhotos.Where(p => p.PartId == partId).OrderBy(p => p.Position).Select(p => p.Id).ToListAsync(ct);

        var listing = new ChannelListing(connection.TenantId, partId, connectionId, clock.GetUtcNow());
        var quantity = Math.Max(0, part.Available);
        var externalId = await new LoggedChannelConnector(connector, log, partId)
            .PublishAsync(new ListingDraft(part.Id, part.InternalCode, part.Title, part.Description, part.Price, quantity, part.Condition, photos, mercadoLivre), ct);
        listing.Published(externalId, quantity, part.Price, clock.GetUtcNow());
        if (mercadoLivre is not null) listing.SetChannelData(MercadoLivre.MercadoLivreConnector.Serialize(mercadoLivre));
        db.ChannelListings.Add(listing);
        await db.SaveChangesAsync(ct);
        return listing;
    }
}
