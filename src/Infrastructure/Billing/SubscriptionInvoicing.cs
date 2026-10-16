using Ecommerce.Application.Billing;
using Ecommerce.Domain.Billing;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Billing;

/// <summary>Datas da cobrança no fuso das lojas (vencimento é dia do calendário, não instante).</summary>
public static class BillingCalendar
{
    private static readonly TimeZoneInfo StoreTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), StoreTimeZone).DateTime);

    public static SubscriptionInvoiceView View(SubscriptionInvoice i, DateOnly today) =>
        new(i.Id, i.Kind, i.Description, i.Amount, i.DueDate, i.Status, i.IsOverdue(today), i.PaidAt, i.PaidVia, i.Note);
}

/// <summary>Geração das mensalidades (RF38 CA1), usada pelo job e pela publicação da loja.</summary>
public static class SubscriptionInvoicing
{
    /// <summary>
    /// Acrescenta ao contexto as mensalidades que faltam até <paramref name="until"/> (sem salvar). Não cobra retroativo
    /// (HIPÓTESE): competência vencida antes de hoje sem fatura — preço zerado na época ou job parado — fica de fora.
    /// Mensalidade zero não gera fatura.
    /// </summary>
    public static async Task<int> AddMissingAsync(PlatformDbContext db, Subscription subscription, Plan plan, DateOnly today, DateOnly until,
        DateTimeOffset now, CancellationToken ct)
    {
        if (plan.MonthlyPrice <= 0) return 0;
        var existing = (await db.SubscriptionInvoices.AsNoTracking()
            .Where(i => i.TenantId == subscription.TenantId && i.Kind == InvoiceKind.Monthly)
            .Select(i => i.Period).ToListAsync(ct)).ToHashSet();
        var added = 0;
        foreach (var (period, dueDate) in subscription.PeriodsUntil(until))
        {
            if (dueDate < today || existing.Contains(period)) continue;
            db.SubscriptionInvoices.Add(SubscriptionInvoice.Monthly(subscription.TenantId, period, dueDate, plan.Name, plan.MonthlyPrice, now));
            added++;
        }
        return added;
    }
}
