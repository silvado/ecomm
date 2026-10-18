using Ecommerce.Application.Reports;
using Ecommerce.Domain.Catalog;
using Ecommerce.Domain.Channels;
using Ecommerce.Domain.Inventory;
using Ecommerce.Domain.Orders;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Reports;

/// <summary>
/// Números do resumo (RF34 CA2), só por consultas:
/// <list type="bullet">
/// <item>Vendas de ontem por canal: pedidos pagos no dia (horário de Brasília) que não foram cancelados nem estão em análise.</item>
/// <item>Peças paradas: ativas, com saldo, cadastradas há mais de 90 dias e sem venda (baixa por pedido) nos últimos 90.</item>
/// <item>Perguntas de compradores sem resposta (RF31).</item>
/// </list>
/// </summary>
public static class DailySummaryFactsQuery
{
    public const int StaleSampleSize = 5;

    internal static readonly TimeZoneInfo StoreTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    private static readonly StockMovementReason[] SaleReasons =
        [StockMovementReason.ReservationConverted, StockMovementReason.SaleAfterExpiry, StockMovementReason.DirectSale];

    private static readonly OrderStatus[] NotSales = [OrderStatus.PendingPayment, OrderStatus.Canceled, OrderStatus.NeedsReview];

    public static DateOnly Yesterday(DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, StoreTimeZone).DateTime).AddDays(-1);

    public static DateTimeOffset StartOf(DateOnly day) =>
        new(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), StoreTimeZone), TimeSpan.Zero);

    public static async Task<DailySummaryFacts> LoadAsync(TenantDbContext db, DateOnly day, DateTimeOffset now, CancellationToken ct)
    {
        var from = StartOf(day);
        var to = StartOf(day.AddDays(1));
        var sales = (await db.CustomerOrders.AsNoTracking()
                .Where(o => o.PaidAt >= from && o.PaidAt < to && !NotSales.Contains(o.Status))
                .GroupBy(o => o.Origin)
                .Select(g => new { Origin = g.Key, Orders = g.Count(), Total = g.Sum(o => o.Total) })
                .ToListAsync(ct))
            .OrderBy(s => s.Origin).Select(s => new ChannelSales(s.Origin, s.Orders, s.Total)).ToList();

        var cutoff = now.AddDays(-DailySummaryFacts.StaleDays);
        var stale = from p in db.Parts.AsNoTracking()
                    join s in db.Stocks.AsNoTracking() on p.Id equals s.PartId
                    where p.Status == PartStatus.Active && s.OnHand - s.Reserved > 0 && p.CreatedAt < cutoff
                          && !db.StockMovements.Any(m => m.PartId == p.Id && m.Delta < 0 && SaleReasons.Contains(m.Reason) && m.At >= cutoff)
                    select new
                    {
                        p.Id, p.InternalCode, p.Title, Available = s.OnHand - s.Reserved, p.CreatedAt,
                        LastSale = db.StockMovements.Where(m => m.PartId == p.Id && m.Delta < 0 && SaleReasons.Contains(m.Reason)).Max(m => (DateTimeOffset?)m.At),
                    };
        var staleCount = await stale.CountAsync(ct);
        var sample = (await stale.OrderBy(x => x.LastSale ?? x.CreatedAt).ThenBy(x => x.InternalCode).Take(StaleSampleSize).ToListAsync(ct))
            .Select(x => new StalePart(x.Id, x.InternalCode, x.Title, x.Available, (int)(now - (x.LastSale ?? x.CreatedAt)).TotalDays))
            .ToList();

        var pending = db.BuyerQuestions.AsNoTracking().Where(q => q.Status == BuyerQuestionStatus.Pending);
        var pendingCount = await pending.CountAsync(ct);
        var oldest = await pending.MinAsync(q => (DateTimeOffset?)q.AskedAt, ct);

        return new DailySummaryFacts(day, sales, staleCount, sample, pendingCount, oldest);
    }
}
