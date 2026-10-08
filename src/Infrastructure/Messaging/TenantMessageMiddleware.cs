using Ecommerce.Infrastructure.Tenancy;
using Wolverine;

namespace Ecommerce.Infrastructure.Messaging;

/// <summary>
/// Abre o escopo do tenant a partir do envelope antes de qualquer handler (ADR-0001/0002).
/// Mensagem sem tenant mantém o contexto vazio: o RLS não devolve dados de tenant (falha fechada).
/// </summary>
public static class TenantMessageMiddleware
{
    public static void Before(Envelope envelope, TenantScope tenant)
    {
        if (Guid.TryParse(envelope.TenantId, out var tenantId))
            tenant.Set(tenantId);
    }
}
