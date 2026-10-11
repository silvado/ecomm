namespace Ecommerce.Application.Payments;

// Pagamento (RF15) pela fila: o webhook só grava a notificação; a consulta ao gateway e a baixa acontecem no handler,
// com o tenant no envelope e retentativas do Wolverine se o gateway falhar.

/// <summary>Grava a notificação (idempotente) e agenda o processamento na mesma transação (outbox).</summary>
public sealed record RecordPaymentNotification(string Gateway, string IdempotencyKey, string PaymentId);

public enum RecordOutcome
{
    Recorded,
    /// <summary>Mesma notificação entregue de novo: já gravada, nada a fazer.</summary>
    Duplicate,
}

/// <summary>Consulta o pagamento no gateway e aplica ao pedido (pago, em análise ou só registro do status).</summary>
public sealed record ProcessPaymentNotification(Guid EventId);
