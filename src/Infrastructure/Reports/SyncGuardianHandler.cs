using Ecommerce.Application.Ai;
using Ecommerce.Application.Email;
using Ecommerce.Application.Reports;
using Ecommerce.Domain.Reports;
using Ecommerce.Infrastructure.Email;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Ecommerce.Infrastructure.Reports;

/// <summary>
/// Guardião da sincronização da loja do escopo (RF33), na transação do Wolverine: problemas das consultas, só os ainda
/// não avisados nas últimas 24 horas; explicação da IA conferida (ou a fixa) e ação das regras; um e-mail por Dono pela
/// fila. IA fora do ar não impede o alerta.
/// </summary>
public static class SyncGuardianHandler
{
    public static async Task<SyncAlertOutcome> Handle(CheckSyncHealth command, TenantDbContext db, IAiCompletion ai, EmailBranding branding,
        IOptions<PlatformOptions> platform, IMessageBus bus, TimeProvider clock, CancellationToken ct)
    {
        var tenantId = db.RequireTenantId();
        var enabled = await db.StoreSettings.AsNoTracking().Select(s => (bool?)s.SyncAlertsEnabled).FirstOrDefaultAsync(ct) ?? true;
        var owners = command.Owners.Select(o => o.Email.Trim()).Where(e => e.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!enabled || owners.Count == 0) return SyncAlertOutcome.Skipped;

        // Duas rodadas ao mesmo tempo esperam aqui: a segunda vê o alerta da primeira e não avisa em dobro.
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext('sync_guardian'), hashtext({tenantId.ToString()}))", ct);

        var now = clock.GetUtcNow();
        var problems = SyncGuardianRules.ToAlert(await SyncGuardianQuery.LoadAsync(db, now, ct), await SyncGuardianQuery.RecentlyAlertedAsync(db, now, ct))
            .Take(SyncAlert.MaxProblems).ToList();
        if (problems.Count == 0) return SyncAlertOutcome.Skipped;

        IReadOnlyDictionary<int, string> texts = new Dictionary<int, string>();
        try
        {
            texts = SyncGuardianWriter.Read((await ai.CompleteAsync(SyncGuardianWriter.Request(problems), ct)).Output, problems.Count);
        }
        catch (AiException)
        {
            // Sem explicação da IA: vale a explicação fixa (a chamada fica no registro de IA).
        }
        var diagnoses = problems.Select((p, i) => SyncGuardianRules.Diagnose(p, texts.GetValueOrDefault(i))).ToList();
        var aiUsed = problems.Select((p, i) => SyncGuardianRules.AcceptExplanation(texts.GetValueOrDefault(i), p) is not null).Any(ok => ok);

        var brand = await branding.ForCurrentStoreAsync(ct);
        var panel = platform.Value.PanelUrl is { Length: > 0 } url ? url : null;
        db.SyncAlerts.Add(new SyncAlert(tenantId, problems.Select(p => p.Key), owners.Count, aiUsed, now));
        foreach (var owner in owners)
            await bus.PublishAsync(new SendSyncAlertEmail(SyncAlertEmail.Build(brand, owner, diagnoses, panel)), new DeliveryOptions { TenantId = tenantId.ToString() });
        return SyncAlertOutcome.Sent;
    }

    public static Task Handle(SendSyncAlertEmail command, IEmailSender sender, CancellationToken ct) =>
        sender.SendAsync(command.Message, "alerta da sincronização", null, ct);
}
