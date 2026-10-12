using Ecommerce.Application.Common;
using Ecommerce.Infrastructure.Tenancy;
using Wolverine;

namespace Ecommerce.Infrastructure.Messaging;

/// <summary>
/// Abre o escopo do tenant a partir do envelope antes de qualquer handler (ADR-0001/0002).
/// Mensagem sem tenant mantém o contexto vazio: o RLS não devolve dados de tenant (falha fechada).
/// Também fixa a correlação e o tenant do contexto de diagnóstico (logs e log de integração — RNF06, RF29).
/// </summary>
public static class TenantMessageMiddleware
{
    public static void Before(Envelope envelope, TenantScope tenant)
    {
        if (Guid.TryParse(envelope.TenantId, out var tenantId))
        {
            tenant.Set(tenantId);
            Correlation.TenantId = tenantId;
        }
        // Mensagem da fila carrega a correlação de quem a gerou; chamada direta já vem com a da requisição.
        Correlation.Current = envelope.CorrelationId ?? Correlation.Current ?? envelope.Id.ToString("N");
    }
}
