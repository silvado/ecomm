namespace Ecommerce.Application.Tenancy;

/// <summary>
/// Tenant do escopo atual (requisição, mensagem ou job). Nulo = nenhum tenant: o banco não retorna dados de tenant (falha fechada).
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
}
