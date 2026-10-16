using Ecommerce.Application.Common;
using Ecommerce.Application.Platform;
using Ecommerce.Application.Tenancy;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Ecommerce.Infrastructure.Platform;

/// <summary>
/// Grava a auditoria no mesmo <see cref="PlatformDbContext"/> de quem chama: as mudanças pendentes da ação e o registro
/// entram no mesmo SaveChanges (ou os dois, ou nenhum).
/// </summary>
public sealed class PlatformAudit(PlatformDbContext db, PlatformActor actor, TimeProvider clock) : IPlatformAudit
{
    public async Task RecordAsync(string action, Guid? tenantId = null, string? details = null, CancellationToken ct = default)
    {
        db.PlatformAuditLog.Add(new PlatformAuditEntry(actor.AdminId, actor.Email, action, tenantId, Redaction.Text(details, PlatformAuditEntry.DetailsMaxLength),
            actor.Ip, Correlation.Current, clock.GetUtcNow()));
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Painel da plataforma (RF07 CA3): lojas, planos e auditoria — só o banco <c>plataforma</c>.</summary>
public sealed class PlatformConsoleService(
    PlatformDbContext db, ITenantProvisioning provisioning, IPlatformAudit audit, ITenantCatalog catalog, IMemoryCache cache,
    Wolverine.IMessageBus bus, TimeProvider clock) : IPlatformConsole
{
    public async Task<IReadOnlyList<PlatformTenantItem>> ListTenantsAsync(string? search, string? status, CancellationToken ct = default)
    {
        var query = db.Tenants.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var like = Catalog.CatalogFilters.Contains(search.Trim());
            // CNPJ só por inteiro (com ou sem máscara): a coluna é convertida e não aceita busca parcial.
            if (Cnpj.TryParse(search.Trim().ToUpperInvariant(), out var parsedCnpj) && parsedCnpj is { } cnpj) query = query.Where(t => t.Cnpj == cnpj);
            else query = query.Where(t => EF.Functions.ILike(t.Slug, like, "\\") || EF.Functions.ILike(t.TradeName, like, "\\") || EF.Functions.ILike(t.LegalName, like, "\\")
                || t.Domains.Any(d => EF.Functions.ILike(d.Host, like, "\\")));
        }
        if (Enum.TryParse<TenantStatus>(status, ignoreCase: true, out var parsed)) query = query.Where(t => t.Status == parsed);

        var rows = await query.OrderBy(t => t.TradeName).Take(200).Select(t => new
        {
            t.Id, t.Slug, t.TradeName, t.Cnpj, t.Status, t.PlanCode, t.CustomStorefrontSince, t.CreatedAt,
            Primary = t.Domains.Where(d => d.IsPrimary).Select(d => d.Host).FirstOrDefault(),
        }).ToListAsync(ct);
        return rows.Select(r => new PlatformTenantItem(r.Id, r.Slug, r.TradeName, r.Cnpj.Value, r.Status.ToString(), r.PlanCode,
            r.Primary ?? string.Empty, r.CustomStorefrontSince is not null, r.CreatedAt)).ToList();
    }

    public async Task<PlatformTenantDetail?> GetTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        var detail = await DetailAsync(tenantId, ct);
        if (detail is not null) await audit.RecordAsync(PlatformAuditActions.TenantViewed, tenantId, ct: ct);
        return detail;
    }

    public async Task<CreateTenantResult> CreateTenantAsync(CreateTenantRequest request, CancellationToken ct = default)
    {
        // Pelo painel, a senha do Dono é sempre gerada (provisória, troca obrigatória): nunca escolhida pelo superadmin.
        var result = await provisioning.CreateAsync(request with { OwnerPassword = null }, ct);
        if (result.Tenant is { } created)
            await audit.RecordAsync(PlatformAuditActions.TenantCreated, created.TenantId,
                $"slug {created.Slug}, dono {created.OwnerEmail}", ct);
        return result;
    }

    public Task<PlatformTenantResult> ChangePlanAsync(Guid tenantId, string planCode, CancellationToken ct = default) =>
        ChangeAsync(tenantId, async tenant =>
        {
            var code = planCode?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!await db.Plans.AnyAsync(p => p.Code == code, ct)) return "Plano inexistente.";
            if (tenant.PlanCode == code) return null;
            var previous = tenant.PlanCode;
            tenant.ChangePlan(code);
            await audit.RecordAsync(PlatformAuditActions.PlanChanged, tenant.Id, $"{previous} → {code}", ct);
            return null;
        }, ct);

    public Task<PlatformTenantResult> SuspendAsync(Guid tenantId, CancellationToken ct = default) =>
        ChangeAsync(tenantId, async tenant =>
        {
            if (tenant.Status is TenantStatus.Suspended) return null;
            if (tenant.Status is TenantStatus.Closed) return "Loja encerrada não pode ser suspensa.";
            // Reativar abre a loja: suspender uma loja em implantação a publicaria sem os passos obrigatórios (RF05 CA3).
            if (tenant.Status is TenantStatus.Onboarding) return "A loja ainda está em implantação: a loja virtual já está fechada até o Dono publicar.";
            var previous = tenant.Status;
            tenant.Suspend();
            await audit.RecordAsync(PlatformAuditActions.TenantSuspended, tenant.Id, $"antes: {previous}", ct);
            await Billing.BillingStanding.PublishChannelResyncAsync(bus, tenant.Id, previous, tenant.Status);
            return null;
        }, ct);

    public Task<PlatformTenantResult> ReactivateAsync(Guid tenantId, CancellationToken ct = default) =>
        ChangeAsync(tenantId, async tenant =>
        {
            if (tenant.Status is TenantStatus.Active) return null;
            // Bloqueio da régua (RF38) sai com o pagamento: reativar à mão seria desfeito na próxima rodada.
            if (tenant.BillingHold)
                return "A loja está bloqueada por fatura em atraso. Registre o pagamento ou cancele a fatura em Cobrança: ela volta sozinha.";
            if (tenant.Status is not TenantStatus.Suspended) return "Só uma loja suspensa pode ser reativada por aqui.";
            var previous = tenant.Status;
            tenant.Activate();
            await audit.RecordAsync(PlatformAuditActions.TenantReactivated, tenant.Id, ct: ct);
            await Billing.BillingStanding.PublishChannelResyncAsync(bus, tenant.Id, previous, tenant.Status);
            return null;
        }, ct);

    public Task<PlatformTenantResult> SetCustomStorefrontAsync(Guid tenantId, bool enabled, CancellationToken ct = default) =>
        ChangeAsync(tenantId, async tenant =>
        {
            if (tenant.IsCustomStorefront == enabled) return null;
            tenant.SetCustomStorefront(enabled, clock.GetUtcNow());
            await audit.RecordAsync(PlatformAuditActions.CustomStorefrontChanged, tenant.Id, enabled ? "ligada" : "desligada", ct);
            return null;
        }, ct);

    public async Task<IReadOnlyList<PlatformPlan>> PlansAsync(CancellationToken ct = default)
    {
        var plans = await db.Plans.AsNoTracking().Include(p => p.Limits).OrderBy(p => p.Id).ToListAsync(ct);
        var features = await db.PlanFeatures.AsNoTracking().ToListAsync(ct);
        var counts = await db.Tenants.Where(t => t.Status != TenantStatus.Closed).GroupBy(t => t.PlanCode)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        return plans.Select(p => new PlatformPlan(p.Code, p.Name,
            p.Limits.OrderBy(l => l.Key).ToDictionary(l => l.Key, l => l.Value),
            features.Where(f => f.PlanCode == p.Code).Select(f => f.Feature).Order().ToList(),
            counts.GetValueOrDefault(p.Code), p.MonthlyPrice)).ToList();
    }

    public async Task<PlatformAuditPage> AuditAsync(PlatformAuditFilter filter, CancellationToken ct = default)
    {
        var page = Math.Max(1, filter.Page);
        var size = Math.Clamp(filter.PageSize, 1, 200);
        var query = db.PlatformAuditLog.AsNoTracking();
        if (filter.TenantId is { } tenantId) query = query.Where(e => e.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(filter.Action)) query = query.Where(e => e.Action == filter.Action);
        if (!string.IsNullOrWhiteSpace(filter.AdminEmail))
        {
            var email = filter.AdminEmail.Trim().ToLowerInvariant();
            query = query.Where(e => e.AdminEmail.Contains(email));
        }

        var rows = await (from e in query
                          join t in db.Tenants on e.TenantId equals t.Id into tenants
                          from t in tenants.DefaultIfEmpty()
                          orderby e.At descending, e.Id descending
                          select new PlatformAuditItem(e.At, e.AdminEmail, e.Action, e.TenantId, t == null ? null : t.Slug, e.Details, e.Ip))
            .Skip((page - 1) * size).Take(size + 1).ToListAsync(ct);
        return new PlatformAuditPage(rows.Take(size).ToList(), page, size, rows.Count > size);
    }

    /// <summary>Carrega a loja, aplica a mudança (que grava a auditoria junto) e limpa os caches que dependem dela.</summary>
    private async Task<PlatformTenantResult> ChangeAsync(Guid tenantId, Func<Tenant, Task<string?>> change, CancellationToken ct)
    {
        var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null) return new PlatformTenantResult(null, "Loja não encontrada.", NotFound: true);
        if (await change(tenant) is { } error) return new PlatformTenantResult(null, error);

        catalog.Invalidate(tenantId);
        cache.Remove(StoreProfileService.PublicKey(tenantId));
        return new PlatformTenantResult(await DetailAsync(tenantId, ct), null);
    }

    private async Task<PlatformTenantDetail?> DetailAsync(Guid tenantId, CancellationToken ct)
    {
        var tenant = await db.Tenants.AsNoTracking().Include(t => t.Domains).SingleOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null) return null;
        var planName = await db.Plans.Where(p => p.Code == tenant.PlanCode).Select(p => p.Name).SingleAsync(ct);
        var now = clock.GetUtcNow();
        var users = await (from m in db.TenantMemberships
                           join u in db.UserAccounts on m.UserId equals u.Id
                           where m.TenantId == tenantId
                           orderby u.Email
                           select new { u.Email, m.Role, u.MustChangePassword, u.LockedUntil }).ToListAsync(ct);
        return new PlatformTenantDetail(tenant.Id, tenant.Slug, tenant.TradeName, tenant.LegalName, tenant.Cnpj.Value,
            tenant.Status.ToString(), tenant.PlanCode, planName, tenant.CreatedAt, tenant.CustomStorefrontSince,
            tenant.Domains.OrderByDescending(d => d.IsPrimary).ThenBy(d => d.Host)
                .Select(d => new PlatformTenantDomain(d.Host, d.Kind.ToString(), d.VerificationStatus.ToString(), d.IsPrimary)).ToList(),
            users.Select(u => new PlatformTenantUser(u.Email, u.Role.ToString(), u.MustChangePassword, u.LockedUntil > now)).ToList(),
            tenant.BillingHold);
    }
}
