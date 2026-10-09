using Ecommerce.Application.Store;
using Ecommerce.Application.Tenancy;
using Ecommerce.Domain.Store;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Ecommerce.Infrastructure.Store;

/// <summary>
/// Perfil da loja do tenant do escopo: nome fantasia no banco <c>plataforma</c>, aparência e textos no banco de lojas (RLS).
/// Loja que nunca salvou a aparência usa o padrão — nada é criado só para ler.
/// </summary>
public sealed class StoreProfileService(
    TenantDbContext db, PlatformDbContext platform, ITenantCatalog catalog, IMemoryCache cache, TimeProvider clock) : IStoreProfileService
{
    /// <summary>Somado ao cache HTTP do storefront, mantém a mudança visível em até 1 min (RF01 CA3).</summary>
    public static readonly TimeSpan PublicTtl = TimeSpan.FromSeconds(30);

    public async Task<StoreProfile> GetAsync(CancellationToken ct = default)
    {
        var tenantId = db.RequireTenantId();
        var tenant = await platform.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenantId, ct);
        var branding = await LoadBrandingAsync(tenantId, ct);
        return new StoreProfile(tenant.Slug, tenant.Cnpj.Value, tenant.LegalName, tenant.TradeName,
            Theme(branding), Texts(branding), branding.ContrastWarnings());
    }

    public async Task<(StoreProfile? Profile, string? Error)> UpdateAsync(UpdateStoreProfile update, CancellationToken ct = default)
    {
        var tenantId = db.RequireTenantId();
        var tenant = await platform.Tenants.SingleAsync(t => t.Id == tenantId, ct);

        // Garante a linha (duas gravações simultâneas da primeira vez não colidem) e edita a versão do banco.
        var now = clock.GetUtcNow();
        var defaults = StoreBranding.Default(tenantId, now);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO store_brandings (tenant_id, primary_color, background_color, text_color, about, return_policy, footer, updated_at)
            VALUES ({tenantId}, {defaults.PrimaryColor}, {defaults.BackgroundColor}, {defaults.TextColor}, '', '', '', {now})
            ON CONFLICT (tenant_id) DO NOTHING
            """, ct);
        var branding = await db.StoreBrandings.SingleAsync(ct);

        try
        {
            tenant.Rename(update.TradeName);
            branding.Update(update.PrimaryColor, update.BackgroundColor, update.TextColor,
                update.About, update.ReturnPolicy, update.Footer, now);
        }
        catch (ArgumentException e)
        {
            return (null, e.Message.Split(" (Parameter", 2)[0]);
        }

        await db.SaveChangesAsync(ct);
        await platform.SaveChangesAsync(ct);
        catalog.Invalidate(tenantId);
        cache.Remove(PublicKey(tenantId));

        return (new StoreProfile(tenant.Slug, tenant.Cnpj.Value, tenant.LegalName, tenant.TradeName,
            Theme(branding), Texts(branding), branding.ContrastWarnings()), null);
    }

    public async Task<PublicStore> GetPublicAsync(CancellationToken ct = default)
    {
        var tenantId = db.RequireTenantId();
        if (cache.TryGetValue(PublicKey(tenantId), out PublicStore? cached)) return cached!;

        var tenant = await catalog.FindByIdAsync(tenantId, ct) ?? throw new InvalidOperationException("Tenant do escopo fora do catálogo.");
        var branding = await LoadBrandingAsync(tenantId, ct);
        var store = new PublicStore(tenant.Slug, tenant.TradeName, Theme(branding), Texts(branding));
        cache.Set(PublicKey(tenantId), store, PublicTtl);
        return store;
    }

    private async Task<StoreBranding> LoadBrandingAsync(Guid tenantId, CancellationToken ct) =>
        await db.StoreBrandings.AsNoTracking().SingleOrDefaultAsync(ct) ?? StoreBranding.Default(tenantId, clock.GetUtcNow());

    private static StoreTheme Theme(StoreBranding b) => new(b.PrimaryColor, b.OnPrimaryColor, b.BackgroundColor, b.TextColor);

    private static StoreTexts Texts(StoreBranding b) => new(b.About, b.ReturnPolicy, b.Footer);

    private static string PublicKey(Guid tenantId) => $"store:public:{tenantId}";
}
