namespace Ecommerce.Domain.Billing;

public enum SubscriptionEventStatus
{
    Received,
    Processed,
    /// <summary>Cobrança inexistente, não paga ou sem fatura correspondente.</summary>
    Ignored,
}

/// <summary>
/// Aviso do gateway da plataforma sobre uma cobrança da assinatura (regra 7 do CLAUDE.md): gravado com chave de
/// idempotência e processado pela fila. Banco <c>plataforma</c>, sem loja (a fatura diz qual é).
/// </summary>
public sealed class SubscriptionEvent
{
    public const int GatewayMaxLength = 40;
    public const int KeyMaxLength = 200;

    private SubscriptionEvent() { }

    public SubscriptionEvent(string gateway, string idempotencyKey, string chargeId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gateway);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(chargeId);
        if (gateway.Length > GatewayMaxLength || idempotencyKey.Length > KeyMaxLength || chargeId.Length > SubscriptionInvoice.ChargeIdMaxLength)
            throw new ArgumentException("Aviso inválido.", nameof(idempotencyKey));
        Id = Guid.CreateVersion7();
        Gateway = gateway;
        IdempotencyKey = idempotencyKey;
        ChargeId = chargeId;
        Status = SubscriptionEventStatus.Received;
        ReceivedAt = now;
    }

    public Guid Id { get; private set; }
    public string Gateway { get; private set; } = string.Empty;
    public string IdempotencyKey { get; private set; } = string.Empty;

    /// <summary>Cobrança a consultar no gateway (nada além disso é aproveitado do aviso).</summary>
    public string ChargeId { get; private set; } = string.Empty;

    public SubscriptionEventStatus Status { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }

    public void Finish(SubscriptionEventStatus status, DateTimeOffset now)
    {
        if (status == SubscriptionEventStatus.Received) throw new ArgumentOutOfRangeException(nameof(status));
        Status = status;
        ProcessedAt = now;
    }
}
