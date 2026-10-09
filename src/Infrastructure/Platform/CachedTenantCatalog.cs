using Ecommerce.Application.Tenancy;
using Ecommerce.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Ecommerce.Infrastructure.Platform;

/// <summary>
/// Catálogo de tenants com cache em memória (RF04 CA1: TTL ≤ 60 s). Hosts desconhecidos também são cacheados,
/// por menos tempo, para que tráfego para domínios inexistentes não chegue ao banco.
/// O host aponta para o id e os dados ficam sob o id: invalidar o tenant vale para todos os seus domínios.
/// Singleton: abre um escopo próprio para cada consulta ao banco.
/// </summary>
public sealed class CachedTenantCatalog(IServiceScopeFactory scopes, IMemoryCache cache) : ITenantCatalog
{
    private static readonly TimeSpan FoundTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan NotFoundTtl = TimeSpan.FromSeconds(10);

    public async ValueTask<TenantDescriptor?> FindByIdAsync(Guid tenantId, CancellationToken ct = default)
    {
        var key = IdKey(tenantId);
        if (cache.TryGetValue(key, out TenantDescriptor? cached)) return cached;

        var descriptor = await QueryAsync(db => db.Tenants.Where(t => t.Id == tenantId), ct);
        cache.Set(key, descriptor, descriptor is null ? NotFoundTtl : FoundTtl);
        return descriptor;
    }

    public async ValueTask<TenantDescriptor?> FindByHostAsync(string host, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var key = $"tenant:host:{TenantDomain.NormalizeHost(host)}";
        if (cache.TryGetValue(key, out Guid? tenantId))
            return tenantId is null ? null : await FindByIdAsync(tenantId.Value, ct);

        var normalized = TenantDomain.NormalizeHost(host);
        var descriptor = await QueryAsync(db => db.Tenants.Where(t => t.Domains.Any(d =>
            d.Host == normalized && d.VerificationStatus == DomainVerificationStatus.Verified)), ct);

        cache.Set(key, descriptor?.Id, descriptor is null ? NotFoundTtl : FoundTtl);
        if (descriptor is not null) cache.Set(IdKey(descriptor.Id), descriptor, FoundTtl);
        return descriptor;
    }

    public void Invalidate(Guid tenantId) => cache.Remove(IdKey(tenantId));

    private static string IdKey(Guid tenantId) => $"tenant:id:{tenantId}";

    private async Task<TenantDescriptor?> QueryAsync(Func<PlatformDbContext, IQueryable<Tenant>> query, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        return await query(db).AsNoTracking()
            .Select(t => new TenantDescriptor(t.Id, t.Slug, t.TradeName, t.Status, t.DatabaseKey))
            .SingleOrDefaultAsync(ct);
    }
}
