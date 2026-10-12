using Ecommerce.Domain.Integrations;

namespace Ecommerce.Application.Integrations;

/// <summary>
/// Registro de chamadas externas (RF29). Grava fora da transação do negócio: a chamada fica registrada mesmo quando a
/// operação que a fez é desfeita.
/// </summary>
public interface IIntegrationLog
{
    Task RecordAsync(string channel, string operation, bool success, TimeSpan duration, string? errorSummary, string? reference,
        Guid? orderId = null, Guid? partId = null, CancellationToken ct = default);

    /// <summary>Liga ao pedido as chamadas desta correlação feitas antes de se saber o pedido (ex.: consulta de pagamento do webhook).</summary>
    Task LinkOrderAsync(Guid orderId, CancellationToken ct = default);
}

public sealed record IntegrationLogView(
    Guid Id, DateTimeOffset At, string Channel, string Operation, bool Success, int DurationMs, string? ErrorSummary, string? Reference,
    Guid? OrderId, long? OrderNumber, Guid? PartId, string? CorrelationId);

/// <param name="Success">Nulo = todas; falso = só as que falharam.</param>
public sealed record IntegrationLogSearch(string? Channel, bool? Success, Guid? OrderId, Guid? PartId, DateOnly? From, DateOnly? To, int Page = 1, int PageSize = 50);

public sealed record IntegrationLogPage(IReadOnlyList<IntegrationLogView> Items, int Total, int Page, int PageSize);

public sealed record MessageFailureView(
    Guid Id, DateTimeOffset FailedAt, string Description, int Attempts, string ErrorType, string? ErrorSummary, MessageFailureStatus Status,
    DateTimeOffset? ReplayedAt, string? ReplayedByEmail, string? CorrelationId);

public sealed record IntegrationSummary(int PendingFailures, int FailedCallsLast24h);

public enum ReplayOutcome
{
    Replayed,
    NotFound,
    AlreadyReplayed,
}

/// <summary>Leituras e reprocessamento da loja do escopo (RLS + filtro global).</summary>
public interface IIntegrationQueries
{
    Task<IntegrationLogPage> SearchAsync(IntegrationLogSearch search, CancellationToken ct = default);

    Task<IReadOnlyList<MessageFailureView>> FailuresAsync(bool pendingOnly, CancellationToken ct = default);

    Task<IntegrationSummary> SummaryAsync(CancellationToken ct = default);

    /// <summary>Manda a mensagem da fila de falhas de volta para a fila (RF28 CA3).</summary>
    Task<ReplayOutcome> ReplayAsync(Guid failureId, Guid userId, CancellationToken ct = default);
}
