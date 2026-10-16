using System.Diagnostics;
using Ecommerce.Application.Ai;
using Ecommerce.Application.Common;
using Ecommerce.Application.Privacy;
using Ecommerce.Application.Tenancy;
using Ecommerce.Domain.Ai;
using Ecommerce.Infrastructure.Billing;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Platform;
using Ecommerce.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ecommerce.Infrastructure.Ai;

/// <summary>
/// RF35 CA1 / regra 4: registra toda chamada de IA — também as que falham — com loja, usuário, funcionalidade, modelo,
/// tokens, custo estimado, entrada resumida e saída. Grava num escopo próprio (como o log de integração): o registro fica
/// mesmo se a operação de quem chamou for desfeita.
/// </summary>
public sealed partial class AuditedAiCompletion(
    IAiCompletion inner, ITenantContext tenant, PanelActor actor, IServiceScopeFactory scopes, IOptions<AiOptions> options, TimeProvider clock,
    ILogger<AuditedAiCompletion> logger) : IAiCompletion
{
    public async Task<AiResult> CompleteAsync(AiRequest request, CancellationToken ct = default)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Chamada de IA sem loja no escopo.");
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await inner.CompleteAsync(request, ct);
            await RecordAsync(tenantId, request, true, result.Model, result.InputTokens, result.OutputTokens, result.Output.GetRawText(), null, watch);
            return result;
        }
        catch (AiInvalidOutputException e)
        {
            await RecordAsync(tenantId, request, false, e.Model ?? options.Value.ModelFor(request.Feature), e.InputTokens, e.OutputTokens, e.RawOutput, e.Message, watch);
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await RecordAsync(tenantId, request, false, options.Value.ModelFor(request.Feature), 0, 0, null, e is AiException ? e.Message : e.GetType().Name, watch);
            throw;
        }
    }

    private async Task RecordAsync(Guid tenantId, AiRequest request, bool success, string model, int inputTokens, int outputTokens, string? output,
        string? error, Stopwatch watch)
    {
        var cost = options.Value.CostOf(model, inputTokens, outputTokens);
        LogCall(logger, request.Feature, model, success, inputTokens, outputTokens, cost);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
            var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
            db.AiInteractions.Add(new AiInteraction(tenantId, actor.UserId == Guid.Empty ? null : actor.UserId, request.Feature, model, success, inputTokens, outputTokens, cost,
                Redaction.Text(request.InputSummary, AiInteraction.SummaryMaxLength) ?? string.Empty, output,
                Redaction.Text(error, AiInteraction.ErrorMaxLength), (int)watch.ElapsedMilliseconds, Correlation.Current, clock.GetUtcNow()));
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception e) when (e is DbUpdateException or InvalidOperationException or Npgsql.NpgsqlException)
        {
            // HIPÓTESE: a resposta já foi paga; não se perde a sugestão por falha do registro, mas fica o alerta.
            LogNotRecorded(logger, e, request.Feature, tenantId);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "IA {Feature} ({Model}): sucesso={Success}, {InputTokens}+{OutputTokens} tokens, US$ {Cost}")]
    private static partial void LogCall(ILogger logger, string feature, string model, bool success, int inputTokens, int outputTokens, decimal cost);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Chamada de IA {Feature} da loja {TenantId} não foi registrada")]
    private static partial void LogNotRecorded(ILogger logger, Exception exception, string feature, Guid tenantId);
}

/// <summary>
/// RF36: contador mensal por loja e funcionalidade. Reserva uma unidade antes de chamar — atômico no banco, como a reserva
/// de estoque (RNF02): chamadas simultâneas nunca passam do limite — e devolve se a chamada falhar (só sucesso conta,
/// decisão do usuário de 16/10/2026). Só o cadastro por foto tem limite (<see cref="AiFeature.PlanLimitKey"/>).
/// </summary>
public sealed class QuotaAiCompletion(IAiCompletion inner, ITenantContext tenant, PlanDirectory plans, IServiceScopeFactory scopes, TimeProvider clock)
    : IAiCompletion
{
    public async Task<AiResult> CompleteAsync(AiRequest request, CancellationToken ct = default)
    {
        if (!AiFeature.All.Contains(request.Feature)) throw new ArgumentException("Funcionalidade de IA desconhecida.", nameof(request));
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Chamada de IA sem loja no escopo.");
        var period = AiUsageCounter.PeriodOf(BillingCalendar.Today(clock));

        int? limit = null;
        if (AiFeature.PlanLimitKey(request.Feature) is { } key)
        {
            var plan = await plans.ForTenantAsync(tenantId, ct);
            limit = plan.Limit(key);
            if (limit is { } max && !await ReserveAsync(tenantId, request.Feature, period, max, ct))
                throw new AiQuotaExceededException(PlanDirectory.LimitMessage(plan, "cadastros com IA por mês", max));
        }
        if (limit is null) await ReserveAsync(tenantId, request.Feature, period, null, ct);

        try
        {
            return await inner.CompleteAsync(request, ct);
        }
        catch
        {
            await ReleaseAsync(tenantId, request.Feature, period);
            throw;
        }
    }

    /// <summary>+1 se ainda couber; sem limite, só conta.</summary>
    private async Task<bool> ReserveAsync(Guid tenantId, string feature, DateOnly period, int? limit, CancellationToken ct)
    {
        if (limit <= 0) return false;
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
        var rows = limit is { } max
            ? await db.Database.SqlQuery<int>($"""
                INSERT INTO ai_usage_counters (tenant_id, feature, period, used) VALUES ({tenantId}, {feature}, {period}, 1)
                ON CONFLICT (tenant_id, feature, period) DO UPDATE SET used = ai_usage_counters.used + 1
                WHERE ai_usage_counters.used < {max}
                RETURNING used AS "Value"
                """).ToListAsync(ct)
            : await db.Database.SqlQuery<int>($"""
                INSERT INTO ai_usage_counters (tenant_id, feature, period, used) VALUES ({tenantId}, {feature}, {period}, 1)
                ON CONFLICT (tenant_id, feature, period) DO UPDATE SET used = ai_usage_counters.used + 1
                RETURNING used AS "Value"
                """).ToListAsync(ct);
        return rows.Count == 1;
    }

    private async Task ReleaseAsync(Guid tenantId, string feature, DateOnly period)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
        await db.AiUsageCounters.Where(c => c.Feature == feature && c.Period == period && c.Used > 0)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Used, c => c.Used - 1), CancellationToken.None);
    }
}

/// <summary>Uso de IA do mês na loja do escopo (RF36 CA1).</summary>
public sealed class AiUsageService(TenantDbContext db, TimeProvider clock) : IAiUsage
{
    public async Task<IReadOnlyDictionary<string, int>> CurrentMonthAsync(CancellationToken ct = default)
    {
        var period = AiUsageCounter.PeriodOf(BillingCalendar.Today(clock));
        return await db.AiUsageCounters.AsNoTracking().Where(c => c.Period == period).ToDictionaryAsync(c => c.Feature, c => c.Used, ct);
    }
}
