using Ecommerce.Application.Catalog;
using Ecommerce.Domain.Catalog;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Catalog.Import;

/// <summary>Prévia e consulta das importações de peças (RF11). A loja vem do escopo (RLS): outra loja não vê nada.</summary>
public sealed class PartImportService(TenantDbContext db, TimeProvider clock) : IPartImports
{
    public async Task<PreviewResult> PreviewAsync(Stream content, string? fileName, Guid userId, CancellationToken ct = default)
    {
        // Lê no máximo 1 byte além do limite: arquivo grande é recusado sem ser carregado inteiro.
        var buffer = new byte[PartImport.MaxFileBytes + 1];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = await content.ReadAsync(buffer.AsMemory(length), ct)) > 0) length += read;
        if (length == 0) return Invalid("Envie a planilha (XLSX ou CSV).");
        if (length > PartImport.MaxFileBytes) return Invalid($"A planilha pode ter no máximo {PartImport.MaxFileBytes / 1024 / 1024} MB.");

        PartSpreadsheet.ParseResult parsed;
        try
        {
            parsed = PartSpreadsheet.Parse(SpreadsheetReader.Read(buffer[..length]));
        }
        catch (SpreadsheetException e)
        {
            return Invalid(e.Message);
        }
        if (parsed.TotalRows == 0) return Invalid(parsed.Issues.Count > 0 ? parsed.Issues[0].Message : "A planilha está vazia.");

        var codes = parsed.Rows.Select(r => r.InternalCode).ToList();
        var existing = codes.Count == 0 ? 0 : await db.Parts.CountAsync(p => codes.Contains(p.InternalCode), ct);

        var import = PartImport.Validate(db.RequireTenantId(), userId, fileName ?? string.Empty, parsed.TotalRows, parsed.Rows, parsed.Issues,
            existing, clock.GetUtcNow());
        db.PartImports.Add(import);
        await db.SaveChangesAsync(ct);
        return new PreviewResult(PreviewOutcome.Validated, View(import));
    }

    public async Task<PartImportView?> GetAsync(Guid importId, CancellationToken ct = default) =>
        await db.PartImports.AsNoTracking().SingleOrDefaultAsync(i => i.Id == importId, ct) is { } import ? View(import) : null;

    public async Task<IReadOnlyList<PartImportView>> RecentAsync(int count, CancellationToken ct = default)
    {
        var imports = await db.PartImports.AsNoTracking().OrderByDescending(i => i.CreatedAt).Take(Math.Clamp(count, 1, 50)).ToListAsync(ct);
        return imports.Select(i => View(i) with { Errors = [], Skipped = [] }).ToList();
    }

    public byte[] Template(TemplateFormat format) => format == TemplateFormat.Csv ? PartSpreadsheet.TemplateCsv() : PartSpreadsheet.TemplateXlsx();

    internal static PartImportView View(PartImport i) =>
        new(i.Id, i.FileName, i.Status, i.CreatedAt, i.StartedAt, i.FinishedAt, i.TotalRows, i.ValidRows, i.ErrorCount, i.ExistingCodesAtPreview,
            i.ProcessedRows, i.ImportedCount, i.Skipped.Count, i.Errors.OrderBy(e => e.Line).ToList(), i.Skipped);

    private static PreviewResult Invalid(string message) => new(PreviewOutcome.Invalid, Message: message);
}
