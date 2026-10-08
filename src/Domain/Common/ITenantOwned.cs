namespace Ecommerce.Domain.Common;

/// <summary>
/// Entidade que pertence a um tenant. Toda tabela correspondente tem <c>tenant_id</c> e política RLS (ADR-0001).
/// </summary>
public interface ITenantOwned
{
    Guid TenantId { get; }
}
