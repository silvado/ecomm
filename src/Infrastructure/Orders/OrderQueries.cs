using Ecommerce.Application.Orders;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Orders;

/// <summary>Pedidos da loja do escopo para o painel. Sem tenant no escopo, o RLS não devolve nada (falha fechada).</summary>
public sealed class OrderQueries(TenantDbContext db) : IOrderQueries
{
    public async Task<OrderPage> SearchAsync(OrderSearch search, CancellationToken ct = default)
    {
        var page = Math.Max(1, search.Page);
        var size = Math.Clamp(search.PageSize, 1, OrderSearch.MaxPageSize);

        var orders = db.CustomerOrders.AsNoTracking();
        if (search.Status is { } status) orders = orders.Where(o => o.Status == status);

        var total = await orders.CountAsync(ct);
        var items = await orders.OrderByDescending(o => o.Number)
            .Select(o => new OrderSummary(o.Id, o.Number, o.Origin, o.Status, o.PlacedAt, o.BuyerName, o.DeliveryMethod,
                o.Items.Sum(i => i.Quantity), o.Total))
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new OrderPage(items, total, page, size);
    }

    public async Task<OrderView?> GetAsync(Guid orderId, CancellationToken ct = default)
    {
        var o = await db.CustomerOrders.AsNoTracking().Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == orderId, ct);
        return o is null ? null : new OrderView(o.Id, o.Number, o.Origin, o.Status, o.PlacedAt, o.PaymentDeadline,
            new OrderBuyerView(o.BuyerName, o.BuyerEmail, o.BuyerPhone, o.BuyerCpf), o.DeliveryMethod, o.Address,
            o.ShippingCarrier, o.ShippingService, o.ShippingDays, o.PickupAddress,
            o.Items.OrderBy(i => i.Title).Select(i => new OrderItemView(i.PartId, i.InternalCode, i.Title, i.UnitPrice, i.Quantity)).ToList(),
            o.ItemsTotal, o.ShippingTotal, o.Total, o.CanceledAt, o.CancelReason);
    }
}
