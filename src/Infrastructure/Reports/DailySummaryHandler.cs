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
/// Resumo diário da loja do escopo (RF34), na transação do Wolverine. Sai uma vez por dia (linha em <c>daily_summaries</c>,
/// única por loja e dia), depois do horário da loja: números das consultas, abertura da IA conferida e um e-mail por Dono
/// pela fila. IA fora do ar não impede o resumo.
/// </summary>
public static class DailySummaryHandler
{
    public static async Task<DailySummaryOutcome> Handle(PrepareDailySummary command, TenantDbContext db, IAiCompletion ai, EmailBranding branding,
        IOptions<PlatformOptions> platform, IMessageBus bus, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var settings = await db.StoreSettings.AsNoTracking().Select(s => new { s.DailySummaryEnabled, s.DailySummaryHour }).FirstOrDefaultAsync(ct);
        var (enabled, hour) = settings is null ? (true, Domain.Inventory.StoreSettings.DefaultDailySummaryHour) : (settings.DailySummaryEnabled, settings.DailySummaryHour);
        var owners = command.Owners.Select(o => o.Email.Trim()).Where(e => e.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!enabled || owners.Count == 0 || !DailySummaryRules.IsDue(TimeZoneInfo.ConvertTime(now, DailySummaryFactsQuery.StoreTimeZone).DateTime, hour))
            return DailySummaryOutcome.Skipped;

        var day = DailySummaryFactsQuery.Yesterday(now);
        if (await db.DailySummaries.AnyAsync(s => s.Day == day, ct)) return DailySummaryOutcome.Skipped;

        var facts = await DailySummaryFactsQuery.LoadAsync(db, day, now, ct);
        string? opening = null;
        try
        {
            opening = DailySummaryRules.AcceptAiText(DailySummaryWriter.Read((await ai.CompleteAsync(DailySummaryWriter.Request(facts), ct)).Output), facts);
        }
        catch (AiException)
        {
            // Sem abertura: o resumo sai só com os números (a chamada fica no registro de IA).
        }

        var brand = await branding.ForCurrentStoreAsync(ct);
        var panel = platform.Value.PanelUrl is { Length: > 0 } url ? url : null;
        // Duas rodadas ao mesmo tempo: a segunda esbarra no índice único e, na nova tentativa, vê o resumo já enviado.
        db.DailySummaries.Add(new DailySummary(db.RequireTenantId(), day, owners.Count, opening is not null, now));
        foreach (var owner in owners)
            await bus.PublishAsync(new SendDailySummaryEmail(DailySummaryEmail.Build(brand, owner, facts, opening, panel)),
                new DeliveryOptions { TenantId = db.RequireTenantId().ToString() });
        return DailySummaryOutcome.Sent;
    }

    public static Task Handle(SendDailySummaryEmail command, IEmailSender sender, CancellationToken ct) =>
        sender.SendAsync(command.Message, "resumo diário", null, ct);
}
