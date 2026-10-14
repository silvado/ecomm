using Ecommerce.Domain.Platform;

namespace Ecommerce.Application.Tenancy;

/// <summary>Dados do tenant necessários em toda requisição/mensagem (vindos do catálogo, com cache).</summary>
/// <param name="PrimaryHost">Host principal da loja (domínio próprio verificado ou o subdomínio da plataforma — RF03).</param>
public sealed record TenantDescriptor(Guid Id, string Slug, string TradeName, TenantStatus Status, string DatabaseKey, string? PrimaryHost = null)
{
    public bool IsStorefrontAvailable => Tenant.IsStorefrontAvailable(Status);
}

/// <summary>Consulta ao catálogo de tenants (banco <c>plataforma</c>). Implementações usam cache curto (RF04 CA1).</summary>
public interface ITenantCatalog
{
    ValueTask<TenantDescriptor?> FindByIdAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Somente domínios verificados. O host é normalizado pela implementação.</summary>
    ValueTask<TenantDescriptor?> FindByHostAsync(string host, CancellationToken ct = default);

    /// <summary>Descarta o que esta instância tem em cache do tenant (ex.: nome fantasia alterado).</summary>
    void Invalidate(Guid tenantId);

    /// <summary>Descarta o que esta instância guardou para o host (domínio próprio verificado ou removido — RF03 CA3/CA5).</summary>
    void InvalidateHost(string host);
}
