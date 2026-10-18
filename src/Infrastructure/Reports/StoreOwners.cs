using Ecommerce.Application.Reports;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Reports;

/// <summary>Donos de cada loja publicada e não suspensa: destinatários do resumo diário (RF34) e do guardião (RF33).</summary>
internal static class StoreOwners
{
    private static readonly TenantStatus[] Eligible = [TenantStatus.Active, TenantStatus.ReadOnly];

    public static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<DailySummaryRecipient>>> LoadAsync(PlatformDbContext db, CancellationToken ct)
    {
        var owners = await (from t in db.Tenants.AsNoTracking()
                            join m in db.TenantMemberships.AsNoTracking() on t.Id equals m.TenantId
                            join u in db.UserAccounts.AsNoTracking() on m.UserId equals u.Id
                            where Eligible.Contains(t.Status) && m.Role == TenantRole.Owner
                            select new { TenantId = t.Id, u.Email }).ToListAsync(ct);
        return owners.GroupBy(o => o.TenantId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<DailySummaryRecipient>)g.Select(o => new DailySummaryRecipient(o.Email)).ToList());
    }
}
