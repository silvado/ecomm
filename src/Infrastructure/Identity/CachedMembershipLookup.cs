using Ecommerce.Application.Identity;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Ecommerce.Infrastructure.Identity;

/// <summary>
/// Vínculo conferido a cada requisição do painel. Cache curto: usuário removido ou rebaixado perde o acesso em até
/// <see cref="Ttl"/> nas outras instâncias; nesta, na hora (<see cref="Invalidate"/>).
/// Singleton: abre um escopo próprio para cada consulta ao banco.
/// </summary>
public sealed class CachedMembershipLookup(IServiceScopeFactory scopes, IMemoryCache cache) : IMembershipLookup
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    public async ValueTask<TenantAccess?> FindAsync(Guid userId, Guid tenantId, CancellationToken ct = default)
    {
        var key = Key(userId, tenantId);
        if (cache.TryGetValue(key, out TenantAccess? cached)) return cached;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var access = await (from m in db.TenantMemberships
                            join t in db.Tenants on m.TenantId equals t.Id
                            where m.UserId == userId && m.TenantId == tenantId && t.Status != TenantStatus.Closed
                            select new TenantAccess(t.Id, t.Slug, t.TradeName, m.Role)).SingleOrDefaultAsync(ct);

        cache.Set(key, access, Ttl);
        return access;
    }

    public void Invalidate(Guid userId, Guid tenantId) => cache.Remove(Key(userId, tenantId));

    private static string Key(Guid userId, Guid tenantId) => $"membership:{userId}:{tenantId}";
}
