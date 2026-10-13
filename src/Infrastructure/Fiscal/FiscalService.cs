using Ecommerce.Application.Fiscal;
using Ecommerce.Application.Storage;
using Ecommerce.Domain.Fiscal;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Fiscal;

/// <summary>Configuração fiscal da loja, dados fiscais da peça, rascunho da emissão e arquivos da NF-e (RF19).</summary>
public sealed class FiscalService(TenantDbContext db, TenantIssuer issuer, IFiscalProviderResolver providers, IFileStorage storage,
    TimeProvider clock) : IFiscalService
{
    public async Task<FiscalSettingsView> GetSettingsAsync(CancellationToken ct = default) =>
        new(await FiscalHandler.FiscalSettingsAsync(db, ct), await issuer.CnpjAsync(db.RequireTenantId(), ct),
            await providers.ResolveAsync(ct) is not null);

    public async Task<FiscalSettingsView> SaveSettingsAsync(FiscalSettings settings, CancellationToken ct = default)
    {
        var tenantId = db.RequireTenantId();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO store_settings (tenant_id, reservation_minutes) VALUES ({tenantId}, {Domain.Inventory.StoreSettings.DefaultReservationMinutes})
            ON CONFLICT (tenant_id) DO NOTHING
            """, ct);
        var row = await db.StoreSettings.SingleAsync(ct);
        row.ConfigureFiscal(settings);
        await db.SaveChangesAsync(ct);
        return await GetSettingsAsync(ct);
    }

    public async Task<bool> SetPartFiscalAsync(Guid partId, PartFiscalData data, CancellationToken ct = default)
    {
        var part = await db.Parts.SingleOrDefaultAsync(p => p.Id == partId, ct);
        if (part is null) return false;
        var settings = await FiscalHandler.FiscalSettingsAsync(db, ct);
        part.SetFiscal(FiscalRules.Normalize(data, settings.Regime), clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<InvoiceDraft?> DraftAsync(Guid orderId, CancellationToken ct = default)
    {
        var order = await db.CustomerOrders.AsNoTracking().Include(o => o.Items).SingleOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null) return null;
        var settings = await FiscalHandler.FiscalSettingsAsync(db, ct);
        var documents = await DocumentsAsync(orderId, ct);
        var prepared = await InvoiceBuilder.PrepareAsync(db, order, settings, null, await providers.ResolveAsync(ct) is not null,
            documents.Any(d => d.Status is FiscalDocumentStatus.Processing or FiscalDocumentStatus.Authorized), ct, checkRecipient: false);
        return new InvoiceDraft(settings.Mode, prepared.Suggested, prepared.Problems, documents);
    }

    public async Task<IReadOnlyList<FiscalDocumentView>> DocumentsAsync(Guid orderId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var documents = await db.FiscalDocuments.AsNoTracking().Where(d => d.OrderId == orderId).OrderByDescending(d => d.RequestedAt).ToListAsync(ct);
        return documents.Select(d => ToView(d, now)).ToList();
    }

    public static FiscalDocumentView ToView(FiscalDocument d, DateTimeOffset now) =>
        new(d.Id, d.Source, d.Status, d.Environment, d.AccessKey, d.Number, d.Series, d.Message, d.RequestedAt, d.AuthorizedAt, d.CanceledAt,
            d.CancelJustification, d.XmlStorageKey is not null, d.DanfeStorageKey is not null, d.CanCancel(now),
            d is { Source: FiscalDocumentSource.Provider, AuthorizedAt: { } at } ? at + FiscalDocument.CancelWindow : null);

    public async Task<(StoredFile File, string FileName)?> FileAsync(Guid documentId, FiscalFile file, CancellationToken ct = default)
    {
        var document = await db.FiscalDocuments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == documentId, ct);
        var key = file == FiscalFile.Xml ? document?.XmlStorageKey : document?.DanfeStorageKey;
        if (document is null || key is null) return null;
        var stored = await storage.GetAsync(StorageArea.Private, key, ct);
        var name = $"NFe{document.AccessKey ?? document.Id.ToString("N")}{(file == FiscalFile.Xml ? ".xml" : ".pdf")}";
        return stored is null ? null : (stored, name);
    }
}
