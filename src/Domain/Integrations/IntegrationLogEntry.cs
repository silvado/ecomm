using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Integrations;

/// <summary>
/// Uma chamada a serviço externo (RF29 CA1): gateway de pagamento, frete e, depois, canais de venda. Sem segredos — o
/// resumo do erro já vem redigido. Retenção de 90 dias (CA2).
/// </summary>
public sealed class IntegrationLogEntry : ITenantOwned
{
    public const int ChannelMaxLength = 40;
    public const int OperationMaxLength = 80;
    public const int ReferenceMaxLength = 100;
    public const int ErrorMaxLength = 500;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    private IntegrationLogEntry() { }

    public IntegrationLogEntry(Guid tenantId, string channel, string operation, bool success, int durationMs, string? errorSummary,
        string? reference, Guid? orderId, Guid? partId, string? correlationId, DateTimeOffset at)
    {
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        Channel = Cut(channel, ChannelMaxLength) ?? "desconhecido";
        Operation = Cut(operation, OperationMaxLength) ?? "desconhecida";
        Success = success;
        DurationMs = Math.Max(0, durationMs);
        ErrorSummary = Cut(errorSummary, ErrorMaxLength);
        Reference = Cut(reference, ReferenceMaxLength);
        OrderId = orderId;
        PartId = partId;
        CorrelationId = Cut(correlationId, 64);
        At = at;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public DateTimeOffset At { get; private set; }

    /// <summary>"mercado-pago", "cielo", "melhor-envio", "fake"…</summary>
    public string Channel { get; private set; } = string.Empty;

    /// <summary>Em linguagem de negócio: "criar cobrança", "consultar pagamento", "cotar frete".</summary>
    public string Operation { get; private set; } = string.Empty;

    public bool Success { get; private set; }
    public int DurationMs { get; private set; }
    public string? ErrorSummary { get; private set; }

    /// <summary>Id no serviço externo (pagamento, anúncio), quando houver.</summary>
    public string? Reference { get; private set; }

    public Guid? OrderId { get; private set; }
    public Guid? PartId { get; private set; }
    public string? CorrelationId { get; private set; }

    private static string? Cut(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length > max ? value[..max] : value;
}
