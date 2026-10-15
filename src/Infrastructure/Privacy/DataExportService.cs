using Ecommerce.Application.Privacy;
using Ecommerce.Application.Storage;
using Ecommerce.Application.Tenancy;
using Ecommerce.Domain.Privacy;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Wolverine;

namespace Ecommerce.Infrastructure.Privacy;

/// <summary>Exportação completa da loja (RF39), pelo painel do Dono: pedir, acompanhar e baixar (cada passo registrado).</summary>
public sealed class DataExportService(
    TenantDbContext db, IFileStorage storage, ITenantCatalog catalog, IPersonalDataAccessLog log, PanelActor actor, IMessageBus bus,
    TimeProvider clock) : IDataExports
{
    public async Task<IReadOnlyList<DataExportView>> ListAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var exports = await db.DataExports.AsNoTracking().OrderByDescending(e => e.RequestedAt).Take(20).ToListAsync(ct);
        return exports.Select(e => View(e, now)).ToList();
    }

    public async Task<(DataExportView? Export, string? Error)> RequestAsync(CancellationToken ct = default)
    {
        var tenantId = db.RequireTenantId();
        var now = clock.GetUtcNow();
        // Pendente esquecida (o processamento caiu no meio): vira falha e libera um novo pedido.
        await db.DataExports.Where(e => e.Status == DataExportStatus.Pending && e.RequestedAt < now - DataExport.StaleAfter)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, DataExportStatus.Failed)
                .SetProperty(e => e.Error, "O processamento foi interrompido. Peça de novo.").SetProperty(e => e.CompletedAt, now), ct);

        var export = new DataExport(tenantId, actor.UserId, actor.Email, now);
        db.DataExports.Add(export);
        try
        {
            // Junto com o registro de acesso (mesmo SaveChanges).
            await log.RecordAsync(PersonalDataActions.ExportRequested, details: $"exportação {export.Id}", ct: ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return (null, "Já existe uma exportação sendo gerada. Aguarde ela ficar pronta.");
        }

        await bus.PublishAsync(new BuildDataExport(export.Id), new DeliveryOptions { TenantId = tenantId.ToString() });
        return (View(export, now), null);
    }

    public async Task<DataExportFile?> DownloadAsync(Guid exportId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var export = await db.DataExports.AsNoTracking().SingleOrDefaultAsync(e => e.Id == exportId, ct);
        if (export is null || !export.CanDownload(now)) return null;
        var file = await storage.GetAsync(StorageArea.Private, export.StorageKey!, ct);
        if (file is null) return null;

        await log.RecordAsync(PersonalDataActions.ExportDownloaded, details: $"exportação {export.Id}", ct: ct);
        var slug = (await catalog.FindByIdAsync(export.TenantId, ct))?.Slug ?? "loja";
        return new DataExportFile(file, $"exportacao-{slug}-{TimeZoneInfo.ConvertTime(export.CompletedAt!.Value, StoreTimeZone):yyyyMMdd-HHmm}.zip");
    }

    private static readonly TimeZoneInfo StoreTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    private static DataExportView View(DataExport e, DateTimeOffset now) =>
        new(e.Id, e.Status, e.RequestedByEmail, e.RequestedAt, e.CompletedAt, e.ExpiresAt, e.SizeBytes, e.Error, e.CanDownload(now));
}

/// <summary>
/// Gera o pacote da exportação (RF39) fora da requisição: monta o ZIP num arquivo temporário e manda para o armazenamento
/// privado. Erro marca a exportação como falha (o Dono pede de novo); não tenta sozinho para não gerar pacotes em série.
/// </summary>
public static partial class DataExportHandler
{
    public static async Task Handle(BuildDataExport command, TenantDbContext db, IFileStorage storage, ITenantCatalog catalog,
        TimeProvider clock, ILoggerFactory loggers, CancellationToken ct)
    {
        var export = await db.DataExports.SingleOrDefaultAsync(e => e.Id == command.ExportId, ct);
        if (export is null || export.Status != DataExportStatus.Pending) return;

        var key = DataExport.Key(export.TenantId, export.Id);
        var path = Path.Combine(Path.GetTempPath(), $"exportacao-{export.Id}.zip");
        try
        {
            await using (var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var storeName = (await catalog.FindByIdAsync(export.TenantId, ct))?.TradeName ?? "Loja";
                await new DataExportBuilder(db, storage).WriteAsync(file, storeName, clock.GetUtcNow(), ct);
                file.Position = 0;
                await storage.PutAsync(StorageArea.Private, key, file, "application/zip", ct);
                export.Complete(file.Length, clock.GetUtcNow());
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogFailed(loggers.CreateLogger(typeof(DataExportHandler)), e, export.Id);
            export.Fail("Não foi possível gerar a exportação. Peça de novo; se repetir, fale com o suporte.", clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Exportação {ExportId} falhou")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid exportId);
}
