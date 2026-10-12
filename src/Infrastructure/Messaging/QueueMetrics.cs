using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Messaging;

/// <summary>
/// Fila em números (RNF06), para o monitoramento da VPS — da plataforma toda, não de uma loja. Lido das tabelas do
/// Wolverine (schema sem RLS). A tabela de entrada não guarda quando a mensagem chegou: "idade" só existe para as
/// agendadas (próxima execução) e para a fila de falhas (a mais antiga).
/// </summary>
public sealed record QueueMetricsView(
    int Pending, int Scheduled, DateTimeOffset? NextScheduledAt, int MaxAttempts, int Outgoing, int DeadLetters, DateTimeOffset? OldestDeadLetterAt,
    DateTimeOffset At);

public sealed class QueueMetrics(TenantDbContext db, TimeProvider clock)
{
    private sealed record IncomingRow(int Pending, int Scheduled, DateTimeOffset? NextScheduledAt, int MaxAttempts);
    private sealed record DeadRow(int Total, DateTimeOffset? Oldest);

    public async Task<QueueMetricsView> ReadAsync(CancellationToken ct = default)
    {
        var incoming = (await db.Database.SqlQueryRaw<IncomingRow>("""
            SELECT count(*) FILTER (WHERE status = 'Incoming')::int AS pending,
                   count(*) FILTER (WHERE status = 'Scheduled')::int AS scheduled,
                   min(execution_time) FILTER (WHERE status = 'Scheduled') AS next_scheduled_at,
                   coalesce(max(attempts) FILTER (WHERE status IN ('Incoming', 'Scheduled')), 0)::int AS max_attempts
            FROM wolverine.wolverine_incoming_envelopes
            """).ToListAsync(ct)).Single();
        var outgoing = (await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM wolverine.wolverine_outgoing_envelopes").ToListAsync(ct)).Single();
        var dead = (await db.Database.SqlQueryRaw<DeadRow>("""
            SELECT count(*)::int AS total, min(sent_at) AS oldest FROM wolverine.wolverine_dead_letters WHERE NOT replayable
            """).ToListAsync(ct)).Single();
        return new QueueMetricsView(incoming.Pending, incoming.Scheduled, incoming.NextScheduledAt, incoming.MaxAttempts, outgoing, dead.Total, dead.Oldest,
            clock.GetUtcNow());
    }
}
