using Ecommerce.Application.Orders;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Orders;

/// <summary>
/// Pedidos da loja do escopo para o painel (RF17): todos os canais, com filtros. Sem tenant no escopo, o RLS não devolve
/// nada (falha fechada). E-mails de quem mexeu no pedido vêm do banco da plataforma (contas de usuário).
/// </summary>
public sealed class OrderQueries(TenantDbContext db, PlatformDbContext platform) : IOrderQueries
{
    /// <summary>Dias do filtro de período são os do lojista.</summary>
    private static readonly TimeZoneInfo StoreTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public async Task<OrderPage> SearchAsync(OrderSearch search, CancellationToken ct = default)
    {
        var page = Math.Max(1, search.Page);
        var size = Math.Clamp(search.PageSize, 1, OrderSearch.MaxPageSize);

        var orders = db.CustomerOrders.AsNoTracking();
        if (search.Statuses is { Count: > 0 } statuses) orders = orders.Where(o => statuses.Contains(o.Status));
        if (search.Origin is { } origin) orders = orders.Where(o => o.Origin == origin);
        if (search.From is { } from)
        {
            var start = StartOfDay(from);
            orders = orders.Where(o => o.PlacedAt >= start);
        }
        if (search.To is { } to)
        {
            var end = StartOfDay(to.AddDays(1));
            orders = orders.Where(o => o.PlacedAt < end);
        }
        if (search.Text?.Trim() is { Length: > 0 } text)
        {
            text = text.Length > OrderSearch.MaxTextLength ? text[..OrderSearch.MaxTextLength] : text;
            var digits = new string(text.Where(char.IsAsciiDigit).ToArray());
            var number = text.TrimStart('#').All(char.IsAsciiDigit) && long.TryParse(text.TrimStart('#'), out var n) ? n : (long?)null;
            // CNPJ pode ser alfanumérico: compara sem pontuação, em maiúsculas.
            var document = new string(text.Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();
            var pattern = $"%{Escape(text)}%";
            orders = orders.Where(o => o.Number == number
                || EF.Functions.ILike(o.BuyerName, pattern, "\\")
                || EF.Functions.ILike(o.BuyerEmail, pattern, "\\")
                || (digits.Length >= 3 && o.BuyerCpf.Contains(digits))
                || (document.Length >= 3 && o.BuyerCnpj != null && o.BuyerCnpj.Contains(document)));
        }

        var total = await orders.CountAsync(ct);
        var items = await orders.OrderByDescending(o => o.PlacedAt).ThenByDescending(o => o.Number)
            .Select(o => new OrderSummary(o.Id, o.Number, o.Origin, o.Status, o.PlacedAt, o.BuyerName, o.DeliveryMethod,
                o.Items.Sum(i => i.Quantity), o.Total))
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new OrderPage(items, total, page, size);
    }

    public async Task<OrderView?> GetAsync(Guid orderId, CancellationToken ct = default)
    {
        var o = await db.CustomerOrders.AsNoTracking().Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == orderId, ct);
        if (o is null) return null;
        var payments = await db.Payments.AsNoTracking().Where(p => p.OrderId == orderId).OrderBy(p => p.CreatedAt)
            .Select(p => new OrderPaymentView(p.Gateway, p.ExternalId, p.Status, p.Amount, p.Method, p.UpdatedAt)).ToListAsync(ct);
        var history = await db.OrderHistory.AsNoTracking().Where(h => h.OrderId == orderId).OrderBy(h => h.At).ThenBy(h => h.Id).ToListAsync(ct);
        var returns = await db.OrderReturns.AsNoTracking().Where(r => r.OrderId == orderId).OrderBy(r => r.At).ToListAsync(ct);
        var userIds = history.Where(h => h.UserId is not null).Select(h => h.UserId!.Value).Concat(returns.Select(r => r.UserId)).Distinct().ToList();
        var emails = userIds.Count == 0
            ? []
            : await platform.UserAccounts.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Email, ct);

        return new OrderView(o.Id, o.Number, o.Origin, o.ExternalId, o.Status, o.PlacedAt, o.PaymentDeadline,
            new OrderBuyerView(o.BuyerName, o.BuyerEmail, o.BuyerPhone, o.BuyerCpf, o.BuyerCnpj), o.DeliveryMethod, o.Address,
            o.ShippingCarrier, o.ShippingService, o.ShippingDays, o.PickupAddress,
            o.Items.OrderBy(i => i.Title).Select(i => new OrderItemView(i.PartId, i.InternalCode, i.Title, i.UnitPrice, i.Quantity,
                Domain.Orders.CustomerOrder.ReturnedQuantity(returns, i.PartId))).ToList(),
            o.ItemsTotal, o.ShippingTotal, o.Total, o.CanceledAt, o.CancelReason, o.CanceledBy, o.PaidAt, o.ReviewReason,
            o.TrackingCode, o.ShippedAt, o.DeliveredAt, o.PaymentMethod, payments,
            history.Select(h => new OrderHistoryView(h.At, h.FromStatus, h.ToStatus, h.Actor,
                h.UserId is { } user ? emails.GetValueOrDefault(user) : null, h.Note)).ToList(),
            o.ReturnedAt,
            returns.Select(r => new OrderReturnView(r.At, r.Reason, r.Note, r.RefundAmount, r.RefundMethod, emails.GetValueOrDefault(r.UserId), r.Lines)).ToList());
    }

    public Task<Guid?> FindByAccessHashAsync(byte[] hash, CancellationToken ct = default) =>
        db.CustomerOrders.AsNoTracking().Where(o => o.AccessTokenHash == hash).Select(o => (Guid?)o.Id).SingleOrDefaultAsync(ct);

    private static DateTimeOffset StartOfDay(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, StoreTimeZone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
