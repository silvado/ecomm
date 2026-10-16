using Ecommerce.Application.Storage;
using Ecommerce.Application.Store;
using Ecommerce.Application.Tenancy;
using Ecommerce.Domain.Platform;
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
        var settings = await TrackedSettingsAsync(tenantId, ct);

        // Loja personalizada (RF40): o formulário volta com a aparência atual; mudar qualquer item dela é recusado.
        if (tenant.IsCustomStorefront && AppearanceChanged(update, tenant.TradeName, branding))
            throw new AppearanceLockedException();

        // Tudo validado antes de gravar: qualquer erro devolve a mensagem e nada muda.
        try
        {
            if (!tenant.IsCustomStorefront)
            {
                tenant.Rename(update.TradeName);
                branding.Update(update.PrimaryColor, update.BackgroundColor, update.TextColor,
                    update.About, update.ReturnPolicy, update.Footer, clock.GetUtcNow());
            }
            if (update.HideOutOfStock is { } hide) settings.SetHideOutOfStock(hide);
            if (update.Shipping is { } shipping) settings.ConfigureShipping(shipping.OriginPostalCode, shipping.PickupEnabled, shipping.PickupAddress);
        }
        catch (ArgumentException e)
        {
            return (null, e.Message.Split(" (Parameter", 2)[0]);
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
            branding.LogoId is { } logoId ? IStoreProfileService.LogoPath(logoId) : null, tenant.PrimaryHost,
            await CustomThemeAsync(tenantId, ct));
        cache.Set(PublicKey(tenantId), store, PublicTtl);
        return store;
    }

    public async Task<PublicPage?> GetPublicPageAsync(string slug, CancellationToken ct = default)
    {
        var tenantId = db.RequireTenantId();
        if (!TenantPage.IsValidSlug(slug) || !await IsCustomStorefrontAsync(tenantId, ct)) return null;
        return await platform.TenantPages.AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.Slug == slug && p.Published)
            .Select(p => new PublicPage(p.Slug, p.Title, p.Html)).SingleOrDefaultAsync(ct);
    }

    /// <summary>RF40 CA1: o tema exclusivo só vale com a loja personalizada ligada (desligar devolve o visual padrão na hora).</summary>
    private async Task<PublicCustomTheme?> CustomThemeAsync(Guid tenantId, CancellationToken ct)
    {
        if (!await IsCustomStorefrontAsync(tenantId, ct)) return null;
        var theme = await platform.TenantThemes.AsNoTracking().SingleOrDefaultAsync(t => t.TenantId == tenantId, ct);
        var pages = await platform.TenantPages.AsNoTracking().Where(p => p.TenantId == tenantId && p.Published)
            .OrderBy(p => p.Position).ThenBy(p => p.Title).Select(p => new PublicPageLink(p.Slug, p.Title)).ToListAsync(ct);
        if (theme is null && pages.Count == 0) return null;

        // Aninhado (CSS nesting): cada regra só vale dentro das áreas marcadas com .custom-theme.
        var css = theme is { Css.Length: > 0 } ? $".custom-theme {{\n{CustomContentSanitizer.EscapeMarkup(theme.Css)}\n}}" : string.Empty;
        return new PublicCustomTheme(css, theme?.HeaderHtml ?? string.Empty, theme?.FooterHtml ?? string.Empty, pages);
    }

    private Task<bool> IsCustomStorefrontAsync(Guid tenantId, CancellationToken ct) =>
        platform.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenantId && t.CustomStorefrontSince != null, ct);

    public async Task<(StoreProfile? Profile, string? Error)> ReplaceLogoAsync(Stream content, CancellationToken ct = default)
    {
        var tenantId = db.RequireTenantId();
        await RequireEditableAppearanceAsync(tenantId, ct);

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
        await RequireEditableAppearanceAsync(tenantId, ct);
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
    public async Task<StoreLegalTextsEditor> GetLegalTextsAsync(CancellationToken ct = default)
    {
        var tenantId = db.RequireTenantId();
        return await LegalEditorAsync(tenantId, await LoadBrandingAsync(tenantId, ct), ct);
    }

    public async Task<(StoreLegalTextsEditor? Texts, string? Error)> UpdateLegalTextsAsync(string? privacyPolicy, string? terms, CancellationToken ct = default)
    {
        var tenantId = db.RequireTenantId();
        var branding = await TrackedBrandingAsync(tenantId, ct);
        try
        {
            branding.UpdateLegalTexts(privacyPolicy, terms, clock.GetUtcNow());
        }
        catch (ArgumentException e)
        {
            return (null, e.Message.Split(" (Parameter", 2)[0]);
        }
        await db.SaveChangesAsync(ct);
        return (await LegalEditorAsync(tenantId, branding, ct), null);
    }

    public async Task<StoreLegalTexts> GetPublicLegalTextsAsync(CancellationToken ct = default)
    {
        var tenantId = db.RequireTenantId();
        var editor = await LegalEditorAsync(tenantId, await LoadBrandingAsync(tenantId, ct), ct);
        return new StoreLegalTexts(
            editor.PrivacyPolicy.Length > 0 ? editor.PrivacyPolicy : editor.DefaultPrivacyPolicy,
            editor.Terms.Length > 0 ? editor.Terms : editor.DefaultTerms);
    }

    private async Task<StoreLegalTextsEditor> LegalEditorAsync(Guid tenantId, StoreBranding branding, CancellationToken ct)
    {
        var tenant = await platform.Tenants.AsNoTracking().Where(t => t.Id == tenantId)
            .Select(t => new { t.TradeName, t.LegalName, t.Cnpj }).SingleAsync(ct);
        var cnpj = FormatCnpj(tenant.Cnpj.Value);
        return new StoreLegalTextsEditor(branding.PrivacyPolicy, branding.Terms,
            LegalTemplates.PrivacyPolicy(tenant.TradeName, tenant.LegalName, cnpj), LegalTemplates.Terms(tenant.TradeName, tenant.LegalName, cnpj));
    }

    /// <summary>00.000.000/0000-00 (vale também para o CNPJ alfanumérico: mesmas posições).</summary>
    private static string FormatCnpj(string value) =>
        value.Length == 14 ? $"{value[..2]}.{value[2..5]}.{value[5..8]}/{value[8..12]}-{value[12..]}" : value;

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

    /// <summary>Garante a linha de configurações (valores padrão) e devolve a versão rastreada.</summary>
    private async Task<Domain.Inventory.StoreSettings> TrackedSettingsAsync(Guid tenantId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO store_settings (tenant_id, reservation_minutes) VALUES ({tenantId}, {Domain.Inventory.StoreSettings.DefaultReservationMinutes})
            ON CONFLICT (tenant_id) DO NOTHING
            """, ct);
        return await db.StoreSettings.SingleAsync(ct);
    }

    private async Task<StoreProfile> ProfileAsync(Guid tenantId, StoreBranding branding, CancellationToken ct)
    {
        var tenant = await platform.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenantId, ct);
        var settings = await db.StoreSettings.AsNoTracking().SingleOrDefaultAsync(ct);
        return new StoreProfile(tenant.Slug, tenant.Cnpj.Value, tenant.LegalName, tenant.TradeName,
            Theme(branding), Texts(branding), branding.ContrastWarnings(), branding.LogoId, settings?.HideOutOfStock ?? false,
            new StoreShipping(settings?.OriginPostalCode, settings?.PickupEnabled ?? false, settings?.PickupAddress), tenant.IsCustomStorefront);
    }

    private async Task RequireEditableAppearanceAsync(Guid tenantId, CancellationToken ct)
    {
        if (await platform.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenantId && t.CustomStorefrontSince != null, ct))
            throw new AppearanceLockedException();
    }

    /// <summary>Comparação tolerante (espaços nas pontas, quebras de linha, maiúsculas nas cores), como o painel reenvia o que leu.</summary>
    private static bool AppearanceChanged(UpdateStoreProfile update, string tradeName, StoreBranding branding)
    {
        static string Text(string? value) => (value ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        static bool Color(string? value, string current) => string.Equals(Text(value), current, StringComparison.OrdinalIgnoreCase);
        return Text(update.TradeName) != tradeName
            || !Color(update.PrimaryColor, branding.PrimaryColor) || !Color(update.BackgroundColor, branding.BackgroundColor)
            || !Color(update.TextColor, branding.TextColor)
            || Text(update.About) != Text(branding.About) || Text(update.ReturnPolicy) != Text(branding.ReturnPolicy)
            || Text(update.Footer) != Text(branding.Footer);
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

    /// <summary>Chave do <see cref="PublicStore"/> em cache; quem muda o host principal (RF03) também a limpa.</summary>
    internal static string PublicKey(Guid tenantId) => $"store:public:{tenantId}";
}
