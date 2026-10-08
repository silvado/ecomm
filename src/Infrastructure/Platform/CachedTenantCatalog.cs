using Ecommerce.Application.Tenancy;
using Ecommerce.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Ecommerce.Infrastructure.Platform;

/// <summary>
/// Catálogo de tenants com cache em memória (RF04 CA1: TTL ≤ 60 s). Hosts desconhecidos também são cacheados,
/// por menos tempo, para que tráfego para domínios inexistentes não chegue ao banco.
/// Singleton: abre um escopo próprio para cada consulta ao banco.
/// </summary>
public sealed class CachedTenantCatalog(IServiceScopeFactory scopes, IMemoryCache cache) : ITenantCatalog
{
    private static readonly TimeSpan FoundTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan NotFoundTtl = TimeSpan.FromSeconds(10);

    public ValueTask<TenantDescriptor?> FindByIdAsync(Guid tenantId, CancellationToken ct = default) =>
        GetOrLoadAsync($"tenant:id:{tenantId}", db => db.Tenants.Where(t => t.Id == tenantId), ct);

    public ValueTask<TenantDescriptor?> FindByHostAsync(string host, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host)) return ValueTask.FromResult<TenantDescriptor?>(null);
        var normalized = TenantDomain.NormalizeHost(host);
        return GetOrLoadAsync($"tenant:host:{normalized}", db => db.Tenants.Where(t => t.Domains.Any(d =>
            d.Host == normalized && d.VerificationStatus == DomainVerificationStatus.Verified)), ct);
    }

    private async ValueTask<TenantDescriptor?> GetOrLoadAsync(
        string key, Func<PlatformDbContext, IQueryable<Tenant>> query, CancellationToken ct)
    {
        if (cache.TryGetValue(key, out TenantDescriptor? cached)) return cached;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var descriptor = await query(db).AsNoTracking()
            .Select(t => new TenantDescriptor(t.Id, t.Slug, t.TradeName, t.Status, t.DatabaseKey))
            .SingleOrDefaultAsync(ct);

        cache.Set(key, descriptor, descriptor is null ? NotFoundTtl : FoundTtl);
        return descriptor;
    }
}
