using Ecommerce.Application.Tenancy;

namespace Ecommerce.Application.Platform;

public sealed record PlatformTenantItem(
    Guid Id, string Slug, string TradeName, string Cnpj, string Status, string PlanCode, string PrimaryHost,
    bool CustomStorefront, DateTimeOffset CreatedAt);

public sealed record PlatformTenantDomain(string Host, string Kind, string Status, bool IsPrimary);

public sealed record PlatformTenantUser(string Email, string Role, bool MustChangePassword, bool LockedOut);

public sealed record PlatformTenantDetail(
    Guid Id, string Slug, string TradeName, string LegalName, string Cnpj, string Status, string PlanCode, string PlanName,
    DateTimeOffset CreatedAt, DateTimeOffset? CustomStorefrontSince,
    IReadOnlyList<PlatformTenantDomain> Domains, IReadOnlyList<PlatformTenantUser> Users);

public sealed record PlatformPlan(string Code, string Name, IReadOnlyDictionary<string, int> Limits, IReadOnlyList<string> Features, int Tenants);

public sealed record PlatformAuditItem(
    DateTimeOffset At, string AdminEmail, string Action, Guid? TenantId, string? TenantSlug, string? Details, string? Ip);

public sealed record PlatformAuditPage(IReadOnlyList<PlatformAuditItem> Items, int Page, int PageSize, bool HasMore);

public sealed record PlatformAuditFilter(Guid? TenantId = null, string? Action = null, string? AdminEmail = null, int Page = 1, int PageSize = 50);

/// <summary>Resultado de uma ação sobre uma loja: a loja atualizada, ou a mensagem para o superadmin.</summary>
public sealed record PlatformTenantResult(PlatformTenantDetail? Tenant, string? Error, bool NotFound = false);

/// <summary>
/// Gestão das lojas pelo superadmin (RF07 CA3, ADR-0007): só dados do banco <c>plataforma</c> — nada de peças, pedidos
/// ou clientes. Toda ação e toda abertura do detalhe de uma loja vão para a auditoria.
/// </summary>
public interface IPlatformConsole
{
    Task<IReadOnlyList<PlatformTenantItem>> ListTenantsAsync(string? search, string? status, CancellationToken ct = default);

    Task<PlatformTenantDetail?> GetTenantAsync(Guid tenantId, CancellationToken ct = default);

    Task<CreateTenantResult> CreateTenantAsync(CreateTenantRequest request, CancellationToken ct = default);

    Task<PlatformTenantResult> ChangePlanAsync(Guid tenantId, string planCode, CancellationToken ct = default);

    Task<PlatformTenantResult> SuspendAsync(Guid tenantId, CancellationToken ct = default);

    Task<PlatformTenantResult> ReactivateAsync(Guid tenantId, CancellationToken ct = default);

    Task<PlatformTenantResult> SetCustomStorefrontAsync(Guid tenantId, bool enabled, CancellationToken ct = default);

    Task<IReadOnlyList<PlatformPlan>> PlansAsync(CancellationToken ct = default);

    Task<PlatformAuditPage> AuditAsync(PlatformAuditFilter filter, CancellationToken ct = default);
}
