using Ecommerce.Application.Channels;
using Ecommerce.Application.Inventory;
using Ecommerce.Application.Storage;
using Ecommerce.Domain.Catalog;
using Ecommerce.Domain.Channels;
using Ecommerce.Domain.Common;
using Ecommerce.Domain.Inventory;
using Ecommerce.Infrastructure.Catalog;
using Ecommerce.Infrastructure.Persistence;
using JasperFx.Core;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;

namespace Ecommerce.Infrastructure.Channels.MercadoLivre;

/// <summary>
/// Importação dos anúncios que a loja já tem no Mercado Livre (RF11 CA2/CA3), pela fila do tenant. Primeiro lê a lista
/// de ids em páginas; depois, em lotes, cada anúncio vira peça vinculada (com as fotos de lá) ou é ligado à peça que já
/// tem o mesmo código. Cada lote é uma transação do Wolverine: peças, saldos, movimentos, vínculos, eventos
/// <see cref="StockChanged"/> e o progresso entram juntos — se o lote cair, é repetido do mesmo ponto, e anúncio já
/// vinculado não duplica. A linha da importação fica travada durante cada passo (sem passos em paralelo).
/// </summary>
public static class ChannelImportHandler
{
    /// <summary>Pequeno: cada anúncio custa duas chamadas ao ML e o download das fotos.</summary>
    public const int BatchSize = 10;

    private const string Reference = "importação do Mercado Livre";

    private const string Reauthorize = "O Mercado Livre pediu nova autorização. Conecte a conta de novo em Canais de venda e importe outra vez.";

    /// <summary>Código cadastrado à mão no mesmo instante esbarra no índice único: o lote é repetido e a linha vira vínculo.</summary>
    public static void Configure(HandlerChain chain) =>
        chain.OnException<DbUpdateException>().ScheduleRetry(1.Seconds(), 5.Seconds(), 30.Seconds());

    public static async Task<(StartChannelImportResult, OutgoingMessages)> Handle(StartChannelImport command, TenantDbContext db, TimeProvider clock,
        CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        var tenantId = db.RequireTenantId();
        // Dois cliques ao mesmo tempo esperam aqui: o segundo vê a importação do primeiro em andamento.
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext('channel_import'), hashtext({tenantId.ToString()}))", ct);
        var running = await db.ChannelImports.AsNoTracking()
            .Where(i => i.Status == ChannelImportStatus.Listing || i.Status == ChannelImportStatus.Processing)
            .OrderByDescending(i => i.CreatedAt).Select(i => (Guid?)i.Id).FirstOrDefaultAsync(ct);
        if (running is not null) return (new StartChannelImportResult(StartChannelImportOutcome.AlreadyRunning, running), outgoing);

        var connection = await db.ChannelConnections.AsNoTracking()
            .Where(c => c.Channel == SalesChannel.MercadoLivre && c.Status == ChannelConnectionStatus.Active)
            .OrderBy(c => c.CreatedAt).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
        if (connection is null) return (new StartChannelImportResult(StartChannelImportOutcome.NotConnected, null), outgoing);

        var import = new ChannelImport(tenantId, connection.Value, command.UserId, clock.GetUtcNow());
        db.ChannelImports.Add(import);
        outgoing.Add(new ListChannelImportPage(import.Id));
        return (new StartChannelImportResult(StartChannelImportOutcome.Started, import.Id), outgoing);
    }

    public static async Task<OutgoingMessages> Handle(ListChannelImportPage command, TenantDbContext db, MercadoLivreApiClient api,
        MercadoLivreCredentials credentials, TimeProvider clock, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        var import = await LockAsync(db, command.ImportId, ct);
        if (import is not { Status: ChannelImportStatus.Listing }) return outgoing;
        var connection = await db.ChannelConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == import.ConnectionId, ct);
        if (connection is null)
        {
            import.Fail("A conta do Mercado Livre foi removida.", clock.GetUtcNow());
            return outgoing;
        }

        string token;
        try { token = await credentials.AccessTokenAsync(connection.Id, ct); }
        catch (ChannelAuthorizationRevokedException)
        {
            import.Fail(Reauthorize, clock.GetUtcNow());
            return outgoing;
        }
        // Fora do ar ou limite de taxa: a exceção sobe e a fila tenta de novo a mesma página.
        var (ids, next) = await api.SearchSellerItemsAsync(token, connection.ExternalAccountId, import.Cursor, ct);
        import.AddPage(ids, next, clock.GetUtcNow());
        if (import.Status == ChannelImportStatus.Listing) outgoing.Add(new ListChannelImportPage(import.Id));
        else if (import.Status == ChannelImportStatus.Processing) outgoing.Add(new ProcessChannelImport(import.Id));
        return outgoing;
    }

    public static async Task<OutgoingMessages> Handle(ProcessChannelImport command, TenantDbContext db, MercadoLivreApiClient api,
        MercadoLivreCredentials credentials, IFileStorage storage, TimeProvider clock, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        var import = await LockAsync(db, command.ImportId, ct);
        var batch = import?.NextBatch(BatchSize) ?? [];
        if (import is null || batch.Count == 0) return outgoing; // mensagem repetida depois do fim: nada a fazer

        string token;
        try { token = await credentials.AccessTokenAsync(import.ConnectionId, ct); }
        catch (ChannelAuthorizationRevokedException)
        {
            import.Fail(Reauthorize, clock.GetUtcNow());
            return outgoing;
        }

        var tenantId = db.RequireTenantId();
        var now = clock.GetUtcNow();
        var linked = (await db.ChannelListings.AsNoTracking().Where(l => l.ConnectionId == import.ConnectionId && l.ExternalId != null && batch.Contains(l.ExternalId))
            .Select(l => l.ExternalId!).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var codesInBatch = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<(ChannelImportItemOutcome, ChannelImportIssue?)>();

        foreach (var externalId in batch)
        {
            if (linked.Contains(externalId))
            {
                results.Add((ChannelImportItemOutcome.AlreadyLinked, null));
                continue;
            }
            var snapshot = await api.GetItemSnapshotAsync(token, externalId, ct);
            if (snapshot is null)
            {
                results.Add(Rejected(externalId, "Anúncio não encontrado no Mercado Livre."));
                continue;
            }
            var (item, reason) = ChannelImportRules.Map(snapshot);
            if (item is null)
            {
                results.Add(Rejected(externalId, reason!));
                continue;
            }
            if (!codesInBatch.Add(item.InternalCode))
            {
                results.Add(Rejected(externalId, $"Outro anúncio desta importação já usa o código {item.InternalCode}."));
                continue;
            }

            var existing = await db.Parts.Where(p => p.InternalCode == item.InternalCode).Select(p => new { p.Id, p.Status }).SingleOrDefaultAsync(ct);
            if (existing is not null)
            {
                results.Add(await LinkAsync(db, import, externalId, snapshot, item, existing.Id, existing.Status, outgoing, now, ct));
                continue;
            }

            Part part;
            try
            {
                part = Part.Create(tenantId, item.InternalCode, item.Details, now);
            }
            catch (ArgumentException e)
            {
                results.Add(Rejected(externalId, e.Message.Split(" (Parameter", 2)[0]));
                continue;
            }
            var photos = await AddPhotosAsync(api, storage, tenantId, part, snapshot.PictureUrls, now, ct);
            db.Parts.Add(part);
            db.Stocks.Add(new Stock(tenantId, part.Id, item.Quantity));
            if (item.Quantity > 0)
                db.StockMovements.Add(new StockMovement(tenantId, part.Id, item.Quantity, StockMovementReason.Adjustment, Reference, reservationId: null, now, import.UserId));
            if (photos == 0)
            {
                // Peça sem foto não fica ativa, e anúncio de peça inativa seria pausado: o anúncio fica sem vínculo.
                outgoing.Add(new StockChanged(part.Id, item.Quantity));
                results.Add((ChannelImportItemOutcome.Created, new ChannelImportIssue(externalId,
                    $"As fotos não puderam ser trazidas: a peça {item.InternalCode} entrou como rascunho e o anúncio não foi vinculado. Envie uma foto, ative a peça e importe de novo.")));
                continue;
            }
            part.Activate(now);
            db.ChannelListings.Add(NewListing(tenantId, part.Id, import.ConnectionId, externalId, snapshot, item, now));
            outgoing.Add(new StockChanged(part.Id, item.Quantity));
            results.Add((ChannelImportItemOutcome.Created, null));
        }

        import.RecordBatch(results, now);
        if (import.Status == ChannelImportStatus.Processing) outgoing.Add(new ProcessChannelImport(import.Id));
        return outgoing;
    }

    /// <summary>Mesmo código de uma peça da loja: liga o anúncio a ela, sem mudar a peça (vale o estoque da loja).</summary>
    private static async Task<(ChannelImportItemOutcome, ChannelImportIssue?)> LinkAsync(TenantDbContext db, ChannelImport import, string externalId,
        ChannelItemSnapshot snapshot, ImportedItem item, Guid partId, PartStatus status, OutgoingMessages outgoing, DateTimeOffset now, CancellationToken ct)
    {
        if (await db.ChannelListings.AnyAsync(l => l.PartId == partId && l.ConnectionId == import.ConnectionId, ct))
            return Rejected(externalId, $"A peça {item.InternalCode} já tem outro anúncio nesta conta do Mercado Livre.");
        if (status != PartStatus.Active)
            return Rejected(externalId, $"Já existe a peça {item.InternalCode}, mas ela não está ativa. Ative a peça e importe de novo para vincular.");
        db.ChannelListings.Add(NewListing(db.RequireTenantId(), partId, import.ConnectionId, externalId, snapshot, item, now));
        var available = await db.Stocks.Where(s => s.PartId == partId).Select(s => s.OnHand - s.Reserved).SingleOrDefaultAsync(ct);
        outgoing.Add(new StockChanged(partId, available)); // leva o estoque da loja ao anúncio, se for diferente
        return (ChannelImportItemOutcome.Linked, new ChannelImportIssue(externalId,
            $"Ligado à peça {item.InternalCode}, que já existia. A peça não mudou e vale o estoque da loja."));
    }

    private static ChannelListing NewListing(Guid tenantId, Guid partId, Guid connectionId, string externalId, ChannelItemSnapshot snapshot, ImportedItem item,
        DateTimeOffset now)
    {
        var listing = new ChannelListing(tenantId, partId, connectionId, now);
        listing.Imported(externalId, item.ListingStatus == ListingStatus.PausedByUser, snapshot.Quantity, snapshot.Price, now);
        if (item.Options.CategoryId.Length > 0) listing.SetChannelData(MercadoLivreConnector.Serialize(item.Options));
        return listing;
    }

    /// <summary>
    /// Fotos do anúncio, na ordem de lá (até 20). Os arquivos são gravados antes do registro da peça; se o lote cair, ficam
    /// sem registro (a repetição grava outros) — custo pequeno diante de baixar tudo de novo a cada tentativa.
    /// </summary>
    private static async Task<int> AddPhotosAsync(MercadoLivreApiClient api, IFileStorage storage, Guid tenantId, Part part, IReadOnlyList<string> urls,
        DateTimeOffset now, CancellationToken ct)
    {
        var added = 0;
        foreach (var url in urls.Take(Part.MaxPhotos))
        {
            if (await api.DownloadPictureAsync(url, ct) is not { } original) continue;
            var contentType = ImageSignature.DetectContentType(original.AsSpan(0, Math.Min(original.Length, ImageSignature.HeaderLength)));
            if (contentType is null) continue;
            var (processed, _) = await PhotoProcessor.ProcessAsync(original, ct);
            if (processed is null) continue;
            var photoId = Guid.CreateVersion7();
            await PutAsync(storage, StorageArea.Private, PartPhoto.OriginalKey(tenantId, part.Id, photoId), original, contentType, ct);
            foreach (var (size, webp) in processed.WebpBySize)
                await PutAsync(storage, StorageArea.Public, PartPhoto.PublicKey(tenantId, part.Id, photoId, size), webp, "image/webp", ct);
            part.AddPhoto(photoId, contentType, now);
            added++;
        }
        return added;
    }

    private static async Task PutAsync(IFileStorage storage, StorageArea area, string key, byte[] bytes, string contentType, CancellationToken ct)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        await storage.PutAsync(area, key, stream, contentType, ct);
    }

    private static (ChannelImportItemOutcome, ChannelImportIssue?) Rejected(string externalId, string message) =>
        (ChannelImportItemOutcome.Rejected, new ChannelImportIssue(externalId, message));

    private static async Task<ChannelImport?> LockAsync(TenantDbContext db, Guid importId, CancellationToken ct)
    {
        await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM channel_imports WHERE id = {importId} FOR UPDATE").ToListAsync(ct);
        return await db.ChannelImports.SingleOrDefaultAsync(i => i.Id == importId, ct);
    }
}
