using Ecommerce.Application.Catalog;
using Ecommerce.Application.Inventory;
using Ecommerce.Domain.Catalog;
using Ecommerce.Domain.Inventory;
using Ecommerce.Infrastructure.Persistence;
using JasperFx.Core;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;

namespace Ecommerce.Infrastructure.Catalog.Import;

/// <summary>
/// Cadastro das peças importadas (RF11 CA3), em lotes pela fila do tenant. Cada lote é uma transação do Wolverine: peças,
/// saldos, movimentos, eventos <see cref="StockChanged"/> e o progresso da importação entram juntos — se o lote cair, nada
/// dele fica e a mensagem é repetida do mesmo ponto. A linha da importação fica travada durante o lote (sem lotes em paralelo).
/// </summary>
public static class PartImportHandler
{
    public const int BatchSize = 100;

    /// <summary>
    /// Código cadastrado à mão no mesmo instante esbarra no índice único: o lote inteiro volta e é repetido numa
    /// execução nova (escopo novo), quando o código já existe e a linha é pulada.
    /// </summary>
    public static void Configure(HandlerChain chain) =>
        chain.OnException<DbUpdateException>().ScheduleRetry(1.Seconds(), 5.Seconds(), 30.Seconds());

    public static async Task<(StartPartImportOutcome, OutgoingMessages)> Handle(StartPartImport command, TenantDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        var import = await LockAsync(db, command.ImportId, ct);
        if (import is null) return (StartPartImportOutcome.NotFound, outgoing);
        if (!import.Confirm(clock.GetUtcNow())) return (StartPartImportOutcome.AlreadyStarted, outgoing);
        if (import.Status == PartImportStatus.Processing) outgoing.Add(new ProcessPartImport(import.Id));
        return (StartPartImportOutcome.Started, outgoing);
    }

    public static async Task<OutgoingMessages> Handle(ProcessPartImport command, TenantDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        var import = await LockAsync(db, command.ImportId, ct);
        var batch = import?.NextBatch(BatchSize) ?? [];
        if (import is null || batch.Count == 0) return outgoing; // mensagem repetida depois do fim: nada a fazer

        var tenantId = db.RequireTenantId();
        var now = clock.GetUtcNow();
        var codes = batch.Select(r => r.InternalCode).Where(c => c.Length > 0).ToList();
        var existing = (await db.Parts.Where(p => codes.Contains(p.InternalCode)).Select(p => p.InternalCode).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var reference = Reference(import.FileName);
        // Código gerado não pode tomar o de outra linha da planilha (deste lote ou dos próximos).
        HashSet<string>? planned = null;

        var imported = 0;
        var skipped = new List<PartImportSkip>();
        var errors = new List<PartImportIssue>();
        foreach (var row in batch)
        {
            if (existing.Contains(row.InternalCode))
            {
                skipped.Add(new PartImportSkip(row.Line, row.InternalCode));
                continue;
            }
            Part part;
            try
            {
                var code = row.InternalCode;
                if (code.Length == 0)
                {
                    planned ??= import.Rows.Select(r => r.InternalCode).Where(c => c.Length > 0).ToHashSet(StringComparer.Ordinal);
                    code = await PartCodes.NextAsync(db, planned, ct);
                }
                part = Part.Create(tenantId, code, row.Details(), now);
            }
            catch (ArgumentException e)
            {
                errors.Add(new PartImportIssue(row.Line, e.Message.Split(" (Parameter", 2)[0]));
                continue;
            }
            db.Parts.Add(part);
            db.Stocks.Add(new Stock(tenantId, part.Id, row.Quantity));
            if (row.Quantity > 0)
                db.StockMovements.Add(new StockMovement(tenantId, part.Id, row.Quantity, StockMovementReason.Adjustment, reference, reservationId: null, now, import.UserId));
            outgoing.Add(new StockChanged(part.Id, row.Quantity));
            imported++;
        }

        import.RecordBatch(batch.Count, imported, skipped, errors, now);
        if (import.Status == PartImportStatus.Processing) outgoing.Add(new ProcessPartImport(import.Id));
        return outgoing;
    }

    private static async Task<PartImport?> LockAsync(TenantDbContext db, Guid importId, CancellationToken ct)
    {
        await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM part_imports WHERE id = {importId} FOR UPDATE").ToListAsync(ct);
        return await db.PartImports.SingleOrDefaultAsync(i => i.Id == importId, ct);
    }

    private static string Reference(string fileName)
    {
        var reference = $"importação {fileName}";
        return reference.Length > 100 ? reference[..100] : reference;
    }
}
