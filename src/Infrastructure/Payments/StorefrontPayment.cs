using Ecommerce.Application.Payments;
using Ecommerce.Application.Tenancy;
using Ecommerce.Domain.Orders;
using Ecommerce.Infrastructure.Orders;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Payments;

/// <summary>
/// Abre o checkout do gateway para um pedido aguardando pagamento (RF15). O gateway devolve o comprador à página do
/// pedido e avisa pelo webhook desta loja — que fica no próprio domínio dela (o Caddy encaminha /api/webhooks/* para a API).
/// </summary>
public sealed class StorefrontPayment(TenantDbContext db, IPaymentGatewayResolver gateways, ITenantContext tenant, TimeProvider clock) : IStorefrontPayment
{
    public async Task<StartPaymentResult> StartAsync(string? token, string storeOrigin, CancellationToken ct = default)
    {
        if (StorefrontCheckout.HashOrNull(token) is not { } hash) return new(StartPaymentOutcome.NotFound, null, null);
        var order = await db.CustomerOrders.AsNoTracking().Include(o => o.Items).SingleOrDefaultAsync(o => o.AccessTokenHash == hash, ct);
        if (order is null) return new(StartPaymentOutcome.NotFound, null, null);
        if (order.Status != OrderStatus.PendingPayment || order.PaymentDeadline <= clock.GetUtcNow())
            return new(StartPaymentOutcome.NotPayable, null, "Este pedido não aguarda mais pagamento.");

        if (await gateways.ResolveAsync(ct) is not { } gateway)
            return new(StartPaymentOutcome.Unavailable, null, "Esta loja ainda não recebe pagamentos online.");

        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Pagamento sem loja no escopo.");
        var origin = storeOrigin.TrimEnd('/');
        try
        {
            var checkout = await gateway.CreateCheckoutAsync(new PaymentCheckoutRequest(
                order.Id, order.Number, order.Items.Select(i => new PaymentCheckoutItem(i.Title, i.Quantity, i.UnitPrice)).ToList(),
                order.ShippingTotal, order.Total, order.BuyerName, order.BuyerEmail, order.PaymentDeadline,
                $"{origin}/pedido", $"{origin}/api/webhooks/pagamentos/{gateway.Name}/{tenantId}"), ct);
            return new(StartPaymentOutcome.Redirect, checkout.RedirectUrl, null);
        }
        catch (PaymentGatewayException)
        {
            return new(StartPaymentOutcome.Unavailable, null, "Não foi possível abrir o pagamento agora. Tente de novo em instantes.");
        }
    }
}
