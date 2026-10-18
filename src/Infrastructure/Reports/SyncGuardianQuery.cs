using Ecommerce.Application.Reports;
using Ecommerce.Domain.Channels;
using Ecommerce.Domain.Integrations;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Reports;

/// <summary>
/// Problemas da sincronização da loja do escopo (RF33 CA1), só de consultas: falhas em segundo plano pendentes das últimas
/// 24 horas, chamadas externas que seguem falhando e canais sem autorização.
/// </summary>
public static class SyncGuardianQuery
{
    /// <summary>Falha do próprio alerta não vira alerta (evita avisar que o aviso falhou).</summary>
    internal const string OwnEmailDescription = "Enviar alerta da sincronização ao Dono";

    private const int MaxCalls = 5_000;

    public static async Task<IReadOnlyList<SyncProblem>> LoadAsync(TenantDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var problems = new List<SyncProblem>();

        var reauth = await db.ChannelConnections.AsNoTracking().Where(c => c.Status == ChannelConnectionStatus.ReauthRequired)
            .Select(c => new { c.Channel, c.StatusReason, At = c.UpdatedAt ?? c.CreatedAt }).ToListAsync(ct);
        problems.AddRange(reauth.Select(c => new SyncProblem($"acesso:{c.Channel}", SyncProblemKind.ChannelReauth, c.Channel, "Autorização do canal", 1, c.At, c.StatusReason, false)));

        var failuresSince = now - SyncGuardianRules.FailureWindow;
        var failures = await db.MessageFailures.AsNoTracking()
            .Where(f => f.Status == MessageFailureStatus.Pending && f.FailedAt >= failuresSince && f.Description != OwnEmailDescription)
            .OrderBy(f => f.FailedAt)
            .Select(f => new { f.Description, f.FailedAt, Error = f.ErrorSummary ?? f.ErrorType }).ToListAsync(ct);
        problems.AddRange(failures.GroupBy(f => f.Description).Select(g => new SyncProblem($"falha:{g.Key}", SyncProblemKind.BackgroundFailure, null, g.Key,
            g.Count(), g.Last().FailedAt, g.Last().Error, SyncGuardianRules.IsListingWork(g.Key))));

        var callsSince = now - SyncGuardianRules.CallWindow;
        var calls = await db.IntegrationLogs.AsNoTracking().Where(l => l.At >= callsSince)
            .OrderByDescending(l => l.At).Take(MaxCalls)
            .Select(l => new { l.Channel, l.Operation, l.Success, l.At, l.ErrorSummary, AboutPart = l.PartId != null }).ToListAsync(ct);
        problems.AddRange(SyncGuardianRules.RepeatedCallFailures(
            calls.Select(c => new IntegrationCall(c.Channel, c.Operation, c.Success, c.At, c.ErrorSummary, c.AboutPart)), now));

        return problems;
    }

    /// <summary>Chaves já avisadas nas últimas 24 horas.</summary>
    public static async Task<IReadOnlyList<string>> RecentlyAlertedAsync(TenantDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var since = now - SyncGuardianRules.RepeatAfter;
        var keys = await db.SyncAlerts.AsNoTracking().Where(a => a.SentAt > since).Select(a => a.ProblemKeys).ToListAsync(ct);
        return keys.SelectMany(k => k).Distinct(StringComparer.Ordinal).ToList();
    }
}
