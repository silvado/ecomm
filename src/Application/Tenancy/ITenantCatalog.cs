using Ecommerce.Domain.Platform;

namespace Ecommerce.Application.Tenancy;

/// <summary>Dados do tenant necessários em toda requisição/mensagem (vindos do catálogo, com cache).</summary>
public sealed record TenantDescriptor(Guid Id, string Slug, string TradeName, TenantStatus Status, string DatabaseKey)
{
    public bool IsStorefrontAvailable => Tenant.IsStorefrontAvailable(Status);
}

/// <summary>Consulta ao catálogo de tenants (banco <c>plataforma</c>). Implementações usam cache curto (RF04 CA1).</summary>
public interface ITenantCatalog
{
    ValueTask<TenantDescriptor?> FindByIdAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Somente domínios verificados. O host é normalizado pela implementação.</summary>
    ValueTask<TenantDescriptor?> FindByHostAsync(string host, CancellationToken ct = default);
}
