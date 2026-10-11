using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Payments;

public enum PaymentStatus
{
    Pending,
    Approved,
    Rejected,
    Refunded,
    Canceled,
}

/// <summary>
/// Pagamento de um pedido como o gateway informou (RF15). Só é criado ou atualizado depois de consultar o gateway —
/// nunca pelo que o navegador ou a notificação dizem (CA3). Único por gateway e id externo.
/// </summary>
public sealed class Payment : ITenantOwned
{
    public const int GatewayMaxLength = 30;
    public const int ExternalIdMaxLength = 100;

    private Payment() { }

    public Payment(Guid tenantId, Guid orderId, string gateway, string externalId, PaymentStatus status, decimal amount, string? method, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gateway);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        OrderId = orderId;
        Gateway = gateway;
        ExternalId = externalId;
        CreatedAt = now;
        Update(status, amount, method, now);
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrderId { get; private set; }
    public string Gateway { get; private set; } = string.Empty;
    public string ExternalId { get; private set; } = string.Empty;
    public PaymentStatus Status { get; private set; }
    public decimal Amount { get; private set; }

    /// <summary>Meio informado pelo gateway (pix, cartão…), só para exibição.</summary>
    public string? Method { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void Update(PaymentStatus status, decimal amount, string? method, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        Status = status;
        Amount = amount;
        Method = method is { Length: > 30 } ? method[..30] : method;
        UpdatedAt = now;
    }
}
