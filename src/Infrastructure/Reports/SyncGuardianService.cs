using Ecommerce.Application.Reports;
using Ecommerce.Domain.Inventory;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Reports;

/// <summary>Guardião da sincronização no painel (RF33): problemas de agora, com a explicação fixa, e o aviso por e-mail.</summary>
public sealed class SyncGuardianService(TenantDbContext db, TimeProvider clock) : ISyncGuardian
{
    public async Task<IReadOnlyList<SyncDiagnosis>> CurrentAsync(CancellationToken ct = default) =>
        (await SyncGuardianQuery.LoadAsync(db, clock.GetUtcNow(), ct)).Select(p => SyncGuardianRules.Diagnose(p)).OrderByDescending(d => d.LastAt).ToList();

    public async Task<SyncGuardianSettingsView> SettingsAsync(CancellationToken ct = default) =>
        new(await db.StoreSettings.AsNoTracking().Select(s => (bool?)s.SyncAlertsEnabled).FirstOrDefaultAsync(ct) ?? true);

    public async Task<SyncGuardianSettingsView> SaveAsync(bool enabled, CancellationToken ct = default)
    {
        var settings = await db.StoreSettings.FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new StoreSettings(db.RequireTenantId());
            db.StoreSettings.Add(settings);
        }
        settings.SetSyncAlerts(enabled);
        await db.SaveChangesAsync(ct);
        return new SyncGuardianSettingsView(settings.SyncAlertsEnabled);
    }
}
