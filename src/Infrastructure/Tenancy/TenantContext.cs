using Ecommerce.Application.Tenancy;

namespace Ecommerce.Infrastructure.Tenancy;

/// <summary>
/// Implementação com escopo (scoped). Definida uma única vez por escopo pela resolução de tenant.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }

    public void Set(Guid tenantId)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant inválido.", nameof(tenantId));
        if (TenantId is not null && TenantId != tenantId)
            throw new InvalidOperationException("O tenant do escopo não pode ser trocado.");
        TenantId = tenantId;
    }
}
