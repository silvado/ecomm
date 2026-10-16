using Ecommerce.Application.Platform;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Ecommerce.Infrastructure.Platform;

/// <summary>
/// Tema e páginas exclusivos da loja personalizada (RF40 CA1) pelo superadmin: tudo passa pela limpeza antes de gravar,
/// e a gravação e a auditoria entram no mesmo SaveChanges. Só banco <c>plataforma</c> (ADR-0007).
/// </summary>
public sealed class PlatformThemeService(
    PlatformDbContext db, ICustomContentSanitizer sanitizer, IPlatformAudit audit, PlatformActor actor, IMemoryCache cache,
    TimeProvider clock) : IPlatformThemeService
{
    public async Task<PlatformTheme?> GetAsync(Guid tenantId, CancellationToken ct = default)
    {
        var tenant = await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId)
            .Select(t => new { t.TradeName, t.CustomStorefrontSince }).SingleOrDefaultAsync(ct);
        if (tenant is null) return null;

        var theme = await db.TenantThemes.AsNoTracking().SingleOrDefaultAsync(t => t.TenantId == tenantId, ct);
        var pages = await db.TenantPages.AsNoTracking().Where(p => p.TenantId == tenantId)
            .OrderBy(p => p.Position).ThenBy(p => p.Title).ToListAsync(ct);
        return new PlatformTheme(tenantId, tenant.TradeName, tenant.CustomStorefrontSince is not null,
            theme?.Css ?? string.Empty, theme?.HeaderHtml ?? string.Empty, theme?.FooterHtml ?? string.Empty,
            theme?.UpdatedAt, theme?.UpdatedBy,
            pages.Select(p => new PlatformThemePage(p.Id, p.Slug, p.Title, p.Html, p.Published, p.Position, p.UpdatedAt, p.UpdatedBy)).ToList());
    }

    public PlatformThemePreview Preview(SavePlatformTheme theme)
    {
        var css = sanitizer.SanitizeCss(theme.Css);
        var header = sanitizer.SanitizeHtml(theme.HeaderHtml);
        var footer = sanitizer.SanitizeHtml(theme.FooterHtml);
        return new PlatformThemePreview(css.Value, header.Value, footer.Value, Merge(("CSS", css), ("Cabeçalho", header), ("Rodapé", footer)));
    }

    public async Task<PlatformThemeResult> SaveThemeAsync(Guid tenantId, SavePlatformTheme theme, CancellationToken ct = default)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, ct)) return PlatformThemeResult.Missing("Loja não encontrada.");

        var clean = Preview(theme);
        var now = clock.GetUtcNow();
        var stored = await db.TenantThemes.SingleOrDefaultAsync(t => t.TenantId == tenantId, ct);
        if (stored is null) db.TenantThemes.Add(stored = new TenantTheme(tenantId, now));
        try
        {
            stored.Update(clean.Css, clean.HeaderHtml, clean.FooterHtml, actor.Email, now);
        }
        catch (ArgumentException e)
        {
            return PlatformThemeResult.Invalid(Message(e));
        }

        await audit.RecordAsync(PlatformAuditActions.ThemeUpdated, tenantId,
            clean.Removed.Count == 0 ? "tema salvo" : $"tema salvo; removido: {string.Join(", ", clean.Removed)}", ct);
        Invalidate(tenantId);
        return new PlatformThemeResult(await GetAsync(tenantId, ct), clean.Removed, null);
    }

    public async Task<PlatformThemeResult> CreatePageAsync(Guid tenantId, SavePlatformPage page, CancellationToken ct = default)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, ct)) return PlatformThemeResult.Missing("Loja não encontrada.");
        if (await db.TenantPages.CountAsync(p => p.TenantId == tenantId, ct) >= TenantPage.MaxPerTenant)
            return PlatformThemeResult.Invalid($"A loja já tem o máximo de {TenantPage.MaxPerTenant} páginas.");

        var html = sanitizer.SanitizeHtml(page.Html);
        TenantPage created;
        try
        {
            created = new TenantPage(tenantId, page.Slug!, page.Title!, html.Value, page.Published, page.Position, actor.Email, clock.GetUtcNow());
        }
        catch (ArgumentException e)
        {
            return PlatformThemeResult.Invalid(Message(e));
        }
        if (await SlugTakenAsync(tenantId, created.Slug, null, ct)) return PlatformThemeResult.Invalid("Já existe uma página com este endereço.");

        db.TenantPages.Add(created);
        return await SavePageAsync(tenantId, created, html.Removed, PlatformAuditActions.PageSaved, ct);
    }

    public async Task<PlatformThemeResult> UpdatePageAsync(Guid tenantId, Guid pageId, SavePlatformPage page, CancellationToken ct = default)
    {
        var stored = await db.TenantPages.SingleOrDefaultAsync(p => p.TenantId == tenantId && p.Id == pageId, ct);
        if (stored is null) return PlatformThemeResult.Missing("Página não encontrada.");

        var html = sanitizer.SanitizeHtml(page.Html);
        try
        {
            stored.Update(page.Slug, page.Title, html.Value, page.Published, page.Position, actor.Email, clock.GetUtcNow());
        }
        catch (ArgumentException e)
        {
            return PlatformThemeResult.Invalid(Message(e));
        }
        if (await SlugTakenAsync(tenantId, stored.Slug, pageId, ct)) return PlatformThemeResult.Invalid("Já existe uma página com este endereço.");

        return await SavePageAsync(tenantId, stored, html.Removed, PlatformAuditActions.PageSaved, ct);
    }

    public async Task<PlatformThemeResult> DeletePageAsync(Guid tenantId, Guid pageId, CancellationToken ct = default)
    {
        var stored = await db.TenantPages.SingleOrDefaultAsync(p => p.TenantId == tenantId && p.Id == pageId, ct);
        if (stored is null) return PlatformThemeResult.Missing("Página não encontrada.");

        db.TenantPages.Remove(stored);
        return await SavePageAsync(tenantId, stored, [], PlatformAuditActions.PageRemoved, ct);
    }

    private async Task<PlatformThemeResult> SavePageAsync(Guid tenantId, TenantPage page, IReadOnlyList<string> removed, string action, CancellationToken ct)
    {
        var details = $"/p/{page.Slug} ({(page.Published ? "publicada" : "rascunho")})";
        if (removed.Count > 0) details += $"; removido: {string.Join(", ", removed)}";
        try
        {
            await audit.RecordAsync(action, tenantId, details, ct);
        }
        catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // Duas gravações simultâneas com o mesmo endereço: a segunda perde.
            return PlatformThemeResult.Invalid("Já existe uma página com este endereço.");
        }
        Invalidate(tenantId);
        return new PlatformThemeResult(await GetAsync(tenantId, ct), removed, null);
    }

    private Task<bool> SlugTakenAsync(Guid tenantId, string slug, Guid? except, CancellationToken ct) =>
        db.TenantPages.AnyAsync(p => p.TenantId == tenantId && p.Slug == slug && p.Id != except, ct);

    private void Invalidate(Guid tenantId) => cache.Remove(StoreProfileService.PublicKey(tenantId));

    private static List<string> Merge(params (string Where, SanitizedContent Content)[] parts) =>
        parts.SelectMany(p => p.Content.Removed.Select(r => $"{p.Where}: {r}")).ToList();

    private static string Message(ArgumentException e) => e.Message.Split(" (Parameter", 2)[0];
}
