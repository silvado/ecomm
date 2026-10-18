using Ecommerce.Application.Catalog;
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
using Microsoft.Extensions.Caching.Memory;
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
        MercadoLivreCredentials credentials, IFileStorage storage, IMemoryCache cache, TimeProvider clock, CancellationToken ct)
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
        var compatibilities = 0;

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
            var (added, vehiclesNote) = await AddCompatibilitiesAsync(db, part, await VehiclesAsync(api, cache, token, externalId, ct), now, ct);
            compatibilities += added;
            db.Parts.Add(part);
            db.Stocks.Add(new Stock(tenantId, part.Id, item.Quantity));
            if (item.Quantity > 0)
                db.StockMovements.Add(new StockMovement(tenantId, part.Id, item.Quantity, StockMovementReason.Adjustment, Reference, reservationId: null, now, import.UserId));
            if (photos == 0)
            {
                // Peça sem foto não fica ativa, e anúncio de peça inativa seria pausado: o anúncio fica sem vínculo.
                outgoing.Add(new StockChanged(part.Id, item.Quantity));
                results.Add((ChannelImportItemOutcome.Created, new ChannelImportIssue(externalId, Join(
                    $"As fotos não puderam ser trazidas: a peça {item.InternalCode} entrou como rascunho e o anúncio não foi vinculado. Envie uma foto, ative a peça e importe de novo.",
                    vehiclesNote))));
                continue;
            }
            part.Activate(now);
            db.ChannelListings.Add(NewListing(tenantId, part.Id, import.ConnectionId, externalId, snapshot, item, now));
            outgoing.Add(new StockChanged(part.Id, item.Quantity));
            results.Add((ChannelImportItemOutcome.Created, vehiclesNote is null ? null : new ChannelImportIssue(externalId, vehiclesNote)));
        }

        import.RecordBatch(results, now, compatibilities);
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

    /// <summary>Veículos do anúncio no ML. <see cref="Unread"/>: além do limite ou sem marca, modelo e ano no catálogo.</summary>
    private sealed record ListingVehicles(IReadOnlyList<ChannelVehicle> Vehicles, int CatalogOnly, int Unread, bool Failed);

    /// <summary>HIPÓTESE: até 200 veículos por anúncio (cada um custa uma chamada ao ML na primeira vez; o ML aceita 200 por envio).</summary>
    public const int MaxVehiclesPerListing = 200;

    /// <summary>Quantos veículos que não entraram aparecem pelo nome no relatório.</summary>
    private const int ListedVehicles = 10;

    private static readonly TimeSpan VehicleCacheFor = TimeSpan.FromDays(1);

    /// <summary>
    /// Lê os veículos do anúncio. O catálogo de veículos do ML é o mesmo para todas as lojas: cada produto fica em cache por
    /// um dia. Se o ML falhar aqui, a peça entra sem os veículos (com aviso) em vez de repetir o lote e baixar as fotos de novo.
    /// </summary>
    private static async Task<ListingVehicles> VehiclesAsync(MercadoLivreApiClient api, IMemoryCache cache, string token, string externalId, CancellationToken ct)
    {
        try
        {
            var (ids, catalogOnly) = await api.ItemCompatibilitiesAsync(token, externalId, ct);
            var vehicles = new List<ChannelVehicle>();
            var unread = Math.Max(0, ids.Count - MaxVehiclesPerListing);
            foreach (var id in ids.Take(MaxVehiclesPerListing))
            {
                var key = $"ml:product:{id}";
                if (!cache.TryGetValue(key, out ChannelVehicle? vehicle))
                {
                    vehicle = await api.VehicleProductAsync(token, id, ct);
                    cache.Set(key, vehicle, VehicleCacheFor);
                }
                if (vehicle is null) unread++;
                else vehicles.Add(vehicle);
            }
            return new ListingVehicles(vehicles, catalogOnly, unread, Failed: false);
        }
        catch (Exception e) when (e is ChannelConnectorException or ChannelValidationException)
        {
            return new ListingVehicles([], 0, 0, Failed: true);
        }
    }

    /// <summary>
    /// Grava na peça nova os veículos do anúncio que casam com a tabela de veículos (<see cref="ChannelCompatibilityRules"/>),
    /// com a origem "importado do ML". Devolve quantas entraram e o aviso do que ficou de fora (nulo se entrou tudo).
    /// </summary>
    private static async Task<(int Added, string? Note)> AddCompatibilitiesAsync(TenantDbContext db, Part part, ListingVehicles read, DateTimeOffset now,
        CancellationToken ct)
    {
        var notes = new List<string>();
        if (read.Failed) notes.Add($"Os veículos do anúncio não puderam ser lidos agora: confira as compatibilidades da peça {part.InternalCode}.");
        var added = 0;
        if (read.Vehicles.Count > 0)
        {
            var names = read.Vehicles.Select(v => v.Brand).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var brandIds = (await db.VehicleBrands.AsNoTracking().Select(b => new { b.Id, b.Name }).ToListAsync(ct))
                .Where(b => names.Any(n => VehicleNames.SameName(n, b.Name))).Select(b => b.Id).ToList();
            var table = brandIds.Count == 0 ? [] : await (from v in db.VehicleVersions.AsNoTracking()
                                                         join m in db.VehicleModels.AsNoTracking() on v.ModelId equals m.Id
                                                         join b in db.VehicleBrands.AsNoTracking() on m.BrandId equals b.Id
                                                         where brandIds.Contains(b.Id) && !v.Discontinued
                                                         select new VehicleCandidate(v.Id, m.Id, b.Name, m.Name, v.Engine, v.YearFrom, v.YearTo)).ToListAsync(ct);
            var result = ChannelCompatibilityRules.Match(read.Vehicles, table);
            var versions = table.ToDictionary(c => c.VersionId);
            var full = false;
            foreach (var match in result.Matches)
            {
                if (part.Compatibilities.Count >= Part.MaxCompatibilities)
                {
                    full = true;
                    break;
                }
                var version = versions[match.VersionId];
                part.AddCompatibility(new VehicleVersionRange(version.VersionId, version.YearFrom, version.YearTo, Discontinued: false),
                    match.YearFrom, match.YearTo, CompatibilitySource.MlImport, now);
                added++;
            }
            if (full) notes.Add($"A peça {part.InternalCode} chegou ao limite de {Part.MaxCompatibilities} compatibilidades: parte dos veículos do anúncio ficou de fora.");
            if (result.Unmatched.Count > 0)
            {
                var listed = string.Join(", ", result.Unmatched.Take(ListedVehicles));
                var more = result.Unmatched.Count > ListedVehicles ? $" e mais {result.Unmatched.Count - ListedVehicles}" : string.Empty;
                notes.Add($"Veículos do anúncio que não estão na tabela de veículos (ou não deu para saber a versão) e não entraram na peça {part.InternalCode}: {listed}{more}. Inclua à mão, se servirem.");
            }
        }
        if (read.Unread > 0)
            notes.Add(read.Unread == 1
                ? $"1 veículo do anúncio não pôde ser lido e não entrou na peça {part.InternalCode}."
                : $"{read.Unread} veículos do anúncio não puderam ser lidos e não entraram na peça {part.InternalCode}.");
        if (read.CatalogOnly > 0)
            notes.Add((read.CatalogOnly == 1 ? "1 veículo do anúncio vem" : $"{read.CatalogOnly} veículos do anúncio vêm")
                + $" do catálogo do Mercado Livre, que não informa quais são: confira as compatibilidades da peça {part.InternalCode}.");
        return (added, notes.Count == 0 ? null : string.Join(' ', notes));
    }

    private static string Join(string first, string? second) => second is null ? first : first + " " + second;

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
