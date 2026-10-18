using Ecommerce.Application.Reports;
using Ecommerce.Domain.Inventory;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Reports;

/// <summary>Resumo diário no painel (RF34): horário, ligar/desligar e prévia dos números de ontem.</summary>
public sealed class DailySummaryService(TenantDbContext db, TimeProvider clock) : IDailySummaries
{
    public async Task<DailySummarySettingsView> SettingsAsync(CancellationToken ct = default)
    {
        var settings = await db.StoreSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        return settings is null
            ? new DailySummarySettingsView(true, StoreSettings.DefaultDailySummaryHour)
            : new DailySummarySettingsView(settings.DailySummaryEnabled, settings.DailySummaryHour);
    }

    public async Task<DailySummarySettingsView> SaveAsync(bool enabled, int hour, CancellationToken ct = default)
    {
        var settings = await db.StoreSettings.FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new StoreSettings(db.RequireTenantId());
            db.StoreSettings.Add(settings);
        }
        settings.ConfigureDailySummary(enabled, hour);
        await db.SaveChangesAsync(ct);
        return new DailySummarySettingsView(settings.DailySummaryEnabled, settings.DailySummaryHour);
    }

    public Task<DailySummaryFacts> PreviewAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        return DailySummaryFactsQuery.LoadAsync(db, DailySummaryFactsQuery.Yesterday(now), now, ct);
    }
}
