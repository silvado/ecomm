using Ecommerce.Application.Integrations;
using Ecommerce.Domain.Integrations;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace Ecommerce.Infrastructure.Integrations;

/// <summary>
/// Log de integração e falhas da loja do escopo (RF28/RF29). O RLS restringe tudo à loja; o reprocessamento só marca a
/// mensagem da própria falha (que a loja enxerga) como reprocessável na fila de falhas do Wolverine.
/// </summary>
public sealed class IntegrationQueries(TenantDbContext db, PlatformDbContext platform, IWolverineRuntime runtime, TimeProvider clock) : IIntegrationQueries
{
    private static readonly TimeZoneInfo StoreTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public async Task<IntegrationLogPage> SearchAsync(IntegrationLogSearch search, CancellationToken ct = default)
    {
        var page = Math.Max(1, search.Page);
        var size = Math.Clamp(search.PageSize, 1, 200);
        var logs = db.IntegrationLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search.Channel)) logs = logs.Where(l => l.Channel == search.Channel);
        if (search.Success is { } success) logs = logs.Where(l => l.Success == success);
        if (search.OrderId is { } orderId) logs = logs.Where(l => l.OrderId == orderId);
        if (search.PartId is { } partId) logs = logs.Where(l => l.PartId == partId);
        if (search.From is { } from)
        {
            var start = StartOfDay(from);
            logs = logs.Where(l => l.At >= start);
        }
        if (search.To is { } to)
        {
            var end = StartOfDay(to.AddDays(1));
            logs = logs.Where(l => l.At < end);
        }

        var total = await logs.CountAsync(ct);
        var items = await (from l in logs
                           join o in db.CustomerOrders on l.OrderId equals o.Id into orders
                           from o in orders.DefaultIfEmpty()
                           orderby l.At descending, l.Id descending
                           select new IntegrationLogView(l.Id, l.At, l.Channel, l.Operation, l.Success, l.DurationMs, l.ErrorSummary, l.Reference,
                               l.OrderId, o == null ? null : o.Number, l.PartId, l.CorrelationId))
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new IntegrationLogPage(items, total, page, size);
    }

    public async Task<IReadOnlyList<MessageFailureView>> FailuresAsync(bool pendingOnly, CancellationToken ct = default)
    {
        var query = db.MessageFailures.AsNoTracking();
        if (pendingOnly) query = query.Where(f => f.Status == MessageFailureStatus.Pending);
        var failures = await query.OrderByDescending(f => f.FailedAt).Take(200).ToListAsync(ct);
        var userIds = failures.Where(f => f.ReplayedBy is not null).Select(f => f.ReplayedBy!.Value).Distinct().ToList();
        var emails = userIds.Count == 0
            ? []
            : await platform.UserAccounts.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Email, ct);
        return failures.Select(f => new MessageFailureView(f.Id, f.FailedAt, f.Description, f.Attempts, f.ErrorType, f.ErrorSummary, f.Status,
            f.ReplayedAt, f.ReplayedBy is { } by ? emails.GetValueOrDefault(by) : null, f.CorrelationId)).ToList();
    }

    public async Task<IntegrationSummary> SummaryAsync(CancellationToken ct = default)
    {
        var since = clock.GetUtcNow().AddDays(-1);
        return new IntegrationSummary(
            await db.MessageFailures.CountAsync(f => f.Status == MessageFailureStatus.Pending, ct),
            await db.IntegrationLogs.CountAsync(l => !l.Success && l.At >= since, ct));
    }

    public async Task<ReplayOutcome> ReplayAsync(Guid failureId, Guid userId, CancellationToken ct = default)
    {
        var failure = await db.MessageFailures.SingleOrDefaultAsync(f => f.Id == failureId, ct);
        if (failure is null) return ReplayOutcome.NotFound;
        if (failure.Status != MessageFailureStatus.Pending) return ReplayOutcome.AlreadyReplayed;

        // O agente de durabilidade do Wolverine devolve à fila as mensagens marcadas (por consulta periódica).
        await runtime.Storage.DeadLetters.MarkDeadLetterEnvelopesAsReplayableAsync([failure.EnvelopeId]);
        failure.MarkReplayed(userId, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return ReplayOutcome.Replayed;
    }

    private static DateTimeOffset StartOfDay(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, StoreTimeZone.GetUtcOffset(local)).ToUniversalTime();
    }
}
