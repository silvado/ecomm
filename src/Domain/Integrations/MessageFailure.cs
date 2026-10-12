using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Integrations;

public enum MessageFailureStatus
{
    /// <summary>Esgotou as tentativas e está na fila de falhas, aguardando a loja.</summary>
    Pending,
    /// <summary>A loja mandou reprocessar: volta para a fila (se falhar de novo, gera outra falha).</summary>
    Replayed,
}

/// <summary>
/// Processamento em segundo plano que esgotou as tentativas (RF28 CA2): fica visível para a loja no painel, com o
/// motivo resumido (sem segredos), até ser reprocessado (CA3). Aponta para a mensagem na fila de falhas do Wolverine.
/// </summary>
public sealed class MessageFailure : ITenantOwned
{
    public const int DescriptionMaxLength = 120;
    public const int ErrorMaxLength = 500;

    private MessageFailure() { }

    public MessageFailure(Guid tenantId, Guid envelopeId, string messageType, string description, int attempts, string errorType,
        string? errorSummary, string? correlationId, DateTimeOffset failedAt)
    {
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        EnvelopeId = envelopeId;
        MessageType = Cut(messageType, 200)!;
        Description = Cut(description, DescriptionMaxLength)!;
        Attempts = attempts;
        ErrorType = Cut(errorType, 200)!;
        ErrorSummary = Cut(errorSummary, ErrorMaxLength);
        CorrelationId = Cut(correlationId, 64);
        FailedAt = failedAt;
        Status = MessageFailureStatus.Pending;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>Id da mensagem na fila de falhas (para reprocessar).</summary>
    public Guid EnvelopeId { get; private set; }

    public string MessageType { get; private set; } = string.Empty;

    /// <summary>O que estava sendo feito, em português (ex.: "Confirmar pagamento no gateway").</summary>
    public string Description { get; private set; } = string.Empty;

    public int Attempts { get; private set; }
    public string ErrorType { get; private set; } = string.Empty;
    public string? ErrorSummary { get; private set; }
    public string? CorrelationId { get; private set; }
    public DateTimeOffset FailedAt { get; private set; }
    public MessageFailureStatus Status { get; private set; }
    public DateTimeOffset? ReplayedAt { get; private set; }
    public Guid? ReplayedBy { get; private set; }

    public void MarkReplayed(Guid userId, DateTimeOffset now)
    {
        if (Status != MessageFailureStatus.Pending) throw new InvalidOperationException("Esta falha já foi reprocessada.");
        Status = MessageFailureStatus.Replayed;
        ReplayedAt = now;
        ReplayedBy = userId;
    }

    private static string? Cut(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length > max ? value[..max] : value;
}
