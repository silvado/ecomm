using Ecommerce.Application.Storage;
using Ecommerce.Application.Store;
using Ecommerce.Application.Tenancy;
using Ecommerce.Domain.Store;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Ecommerce.Infrastructure.Store;

/// <summary>
/// Perfil da loja do tenant do escopo: nome fantasia no banco <c>plataforma</c>, aparência e textos no banco de lojas (RLS),
/// logo no storage público sob <c>{tenantId}/</c>. Loja que nunca salvou a aparência usa o padrão — nada é criado só para ler.
/// </summary>
public sealed class StoreProfileService(
    TenantDbContext db, PlatformDbContext platform, ITenantCatalog catalog, IFileStorage storage,
    IMemoryCache cache, TimeProvider clock) : IStoreProfileService
{
    /// <summary>Somado ao cache HTTP do storefront, mantém a mudança visível em até 1 min (RF01 CA3).</summary>
    public static readonly TimeSpan PublicTtl = TimeSpan.FromSeconds(30);

    public async Task<StoreProfile> GetAsync(CancellationToken ct = default)
    {
        var tenantId = db.RequireTenantId();
        return await ProfileAsync(tenantId, await LoadBrandingAsync(tenantId, ct), ct);
    }

    public async Task<(StoreProfile? Profile, string? Error)> UpdateAsync(UpdateStoreProfile update, CancellationToken ct = default)
    {
        var tenantId = db.RequireTenantId();
        var tenant = await platform.Tenants.SingleAsync(t => t.Id == tenantId, ct);
        var branding = await TrackedBrandingAsync(tenantId, ct);

        try
        {
            tenant.Rename(update.TradeName);
            branding.Update(update.PrimaryColor, update.BackgroundColor, update.TextColor,
                update.About, update.ReturnPolicy, update.Footer, clock.GetUtcNow());
        }
        catch (ArgumentException e)
        {
            return (null, e.Message.Split(" (Parameter", 2)[0]);
        }

        if (update.HideOutOfStock is { } hide)
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO store_settings (tenant_id, reservation_minutes, hide_out_of_stock)
                VALUES ({tenantId}, {Domain.Inventory.StoreSettings.DefaultReservationMinutes}, {hide})
                ON CONFLICT (tenant_id) DO UPDATE SET hide_out_of_stock = EXCLUDED.hide_out_of_stock
                """, ct);
        }

        await db.SaveChangesAsync(ct);
        await platform.SaveChangesAsync(ct);
        Invalidate(tenantId);
        return (await ProfileAsync(tenantId, branding, ct), null);
    }

    public async Task<PublicStore> GetPublicAsync(CancellationToken ct = default)
    {
        var tenantId = db.RequireTenantId();
        if (cache.TryGetValue(PublicKey(tenantId), out PublicStore? cached)) return cached!;

        var tenant = await catalog.FindByIdAsync(tenantId, ct) ?? throw new InvalidOperationException("Tenant do escopo fora do catálogo.");
        var branding = await LoadBrandingAsync(tenantId, ct);
        var store = new PublicStore(tenant.Slug, tenant.TradeName, Theme(branding), Texts(branding),
            branding.LogoId is { } logoId ? IStoreProfileService.LogoPath(logoId) : null);
        cache.Set(PublicKey(tenantId), store, PublicTtl);
        return store;
    }

    public async Task<(StoreProfile? Profile, string? Error)> ReplaceLogoAsync(Stream content, CancellationToken ct = default)
    {
        var tenantId = db.RequireTenantId();

        // Lê no máximo 1 byte além do limite: arquivo grande é recusado sem ser carregado inteiro.
        var buffer = new byte[LogoImage.MaxBytes + 1];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = await content.ReadAsync(buffer.AsMemory(length), ct)) > 0) length += read;
        if (length == 0) return (null, "Envie a imagem do logo.");
        if (length > LogoImage.MaxBytes) return (null, "O logo pode ter no máximo 2 MB.");

        var contentType = LogoImage.DetectContentType(buffer.AsSpan(0, Math.Min(length, LogoImage.HeaderLength)));
        if (contentType is null) return (null, "Formato não aceito: envie PNG, JPG ou WebP.");

        var logoId = Guid.CreateVersion7();
        var key = LogoImage.StorageKey(tenantId, logoId);
        using (var image = new MemoryStream(buffer, 0, length, writable: false))
            await storage.PutAsync(StorageArea.Public, key, image, contentType, ct);

        Guid? previous;
        try
        {
            var branding = await TrackedBrandingAsync(tenantId, ct);
            previous = branding.ReplaceLogo(logoId, contentType, clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            await storage.DeleteAsync(StorageArea.Public, key, CancellationToken.None);
            throw;
        }

        // Só depois de gravar: o logo antigo deixa de ser servido e sai do storage.
        if (previous is { } old) await storage.DeleteAsync(StorageArea.Public, LogoImage.StorageKey(tenantId, old), ct);
        Invalidate(tenantId);
        return (await GetAsync(ct), null);
    }

    public async Task<StoreProfile> RemoveLogoAsync(CancellationToken ct = default)
    {
        var tenantId = db.RequireTenantId();
        var branding = await TrackedBrandingAsync(tenantId, ct);
        var previous = branding.RemoveLogo(clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        if (previous is { } old) await storage.DeleteAsync(StorageArea.Public, LogoImage.StorageKey(tenantId, old), ct);
        Invalidate(tenantId);
        return await ProfileAsync(tenantId, branding, ct);
    }

    public async Task<StoredFile?> GetLogoAsync(Guid? logoId = null, CancellationToken ct = default)
    {
        var tenantId = db.RequireTenantId();
        var current = await db.StoreBrandings.Select(b => b.LogoId).SingleOrDefaultAsync(ct);
        if (current is null || (logoId is not null && logoId != current)) return null;
        return await storage.GetAsync(StorageArea.Public, LogoImage.StorageKey(tenantId, current.Value), ct);
    }

    /// <summary>Garante a linha (gravações simultâneas da primeira vez não colidem) e devolve a versão rastreada.</summary>
    private async Task<StoreBranding> TrackedBrandingAsync(Guid tenantId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var defaults = StoreBranding.Default(tenantId, now);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO store_brandings (tenant_id, primary_color, background_color, text_color, about, return_policy, footer, updated_at)
            VALUES ({tenantId}, {defaults.PrimaryColor}, {defaults.BackgroundColor}, {defaults.TextColor}, '', '', '', {now})
            ON CONFLICT (tenant_id) DO NOTHING
            """, ct);
        return await db.StoreBrandings.SingleAsync(ct);
    }

    private async Task<StoreProfile> ProfileAsync(Guid tenantId, StoreBranding branding, CancellationToken ct)
    {
        var tenant = await platform.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenantId, ct);
        var hideOutOfStock = await db.StoreSettings.Select(s => (bool?)s.HideOutOfStock).SingleOrDefaultAsync(ct) ?? false;
        return new StoreProfile(tenant.Slug, tenant.Cnpj.Value, tenant.LegalName, tenant.TradeName,
            Theme(branding), Texts(branding), branding.ContrastWarnings(), branding.LogoId, hideOutOfStock);
    }

    private void Invalidate(Guid tenantId)
    {
        catalog.Invalidate(tenantId);
        cache.Remove(PublicKey(tenantId));
    }

    private async Task<StoreBranding> LoadBrandingAsync(Guid tenantId, CancellationToken ct) =>
        await db.StoreBrandings.AsNoTracking().SingleOrDefaultAsync(ct) ?? StoreBranding.Default(tenantId, clock.GetUtcNow());

    private static StoreTheme Theme(StoreBranding b) => new(b.PrimaryColor, b.OnPrimaryColor, b.BackgroundColor, b.TextColor);

    private static StoreTexts Texts(StoreBranding b) => new(b.About, b.ReturnPolicy, b.Footer);

    private static string PublicKey(Guid tenantId) => $"store:public:{tenantId}";
}
