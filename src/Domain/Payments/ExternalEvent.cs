using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Payments;

public enum ExternalEventStatus
{
    Received,
    Processed,
    /// <summary>Válido, mas sem efeito aqui (recurso que não é pagamento, pedido de outra origem…).</summary>
    Ignored,
}

/// <summary>
/// Notificação recebida de um serviço externo (webhook), gravada antes de processar (regra 7 do CLAUDE.md, RF26):
/// a chave de idempotência é única por loja e origem, então a mesma notificação entregue de novo não tem efeito.
/// </summary>
public sealed class ExternalEvent : ITenantOwned
{
    public const int SourceMaxLength = 30;
    public const int KeyMaxLength = 200;
    public const int ResourceIdMaxLength = 100;

    private ExternalEvent() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>Quem enviou (ex.: <c>mercado-pago</c>).</summary>
    public string Source { get; private set; } = string.Empty;

    public string IdempotencyKey { get; private set; } = string.Empty;

    /// <summary>Recurso a consultar na origem (ex.: id do pagamento). O conteúdo da notificação nunca é usado como verdade.</summary>
    public string ResourceId { get; private set; } = string.Empty;

    public ExternalEventStatus Status { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }

    public void Finish(ExternalEventStatus status, DateTimeOffset now)
    {
        if (status == ExternalEventStatus.Received) throw new ArgumentOutOfRangeException(nameof(status));
        Status = status;
        ProcessedAt = now;
    }
}
