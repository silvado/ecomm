using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Ecommerce.Application.Orders;
using Ecommerce.Application.Shipping;
using Ecommerce.Application.Tenancy;
using Ecommerce.Domain.Orders;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wolverine;

namespace Ecommerce.Infrastructure.Orders;

/// <summary>
/// Checkout como convidado (RF14 CA3). Tudo é recalculado aqui — preço, estoque, frete e total — e o pedido só é criado se
/// bater com o que o comprador viu; a reserva das peças acontece no <see cref="OrderHandler"/>, na transação do pedido.
/// </summary>
public sealed partial class StorefrontCheckout(TenantDbContext db, IStorefrontCart cart, IMessageBus bus, ITenantContext tenant) : IStorefrontCheckout
{
    [GeneratedRegex("^[A-Za-z0-9_-]{43}$")]
    private static partial Regex TokenFormat();

    public async Task<CheckoutResult> PlaceAsync(CheckoutRequest request, CancellationToken ct = default)
    {
        if (HashOrNull(request.Token) is not { } hash) return CheckoutResult.Invalid("Não foi possível identificar este checkout. Recarregue a página.");
        var items = request.Items ?? [];
        if (items.Count is 0 or > IStorefrontCart.MaxLines) return CheckoutResult.Invalid("O carrinho está vazio.");
        if (request.Buyer is null) return CheckoutResult.Invalid("Informe seus dados.");
        if (request.Delivery is null) return CheckoutResult.Invalid("Escolha entrega ou retirada.");
        if (request.ExpectedTotal is not { } expectedTotal) return CheckoutResult.Invalid("Total do pedido ausente.");

        BuyerDetails buyer;
        DeliveryAddress? address = null;
        try
        {
            buyer = CustomerOrder.NormalizeBuyer(request.Buyer);
            if (request.Delivery.Method == DeliveryMethod.Shipping)
                address = CustomerOrder.NormalizeAddress(request.Delivery.Address ?? throw new ArgumentException("Informe o endereço de entrega."));
        }
        catch (ArgumentException e)
        {
            return CheckoutResult.Invalid(e.Message.Split(" (Parameter", 2)[0]);
        }

        // Envio repetido (duplo clique, queda de rede) cai num destes casos e devolve o pedido já criado:
        // a peça já está reservada por ele (sem saldo) ou o índice único do token recusa o segundo pedido.
        var view = await cart.CheckAsync(items, ct);
        // Sem saldo pode ser o próprio pedido, criado por um envio igual que chegou junto: confere antes de recusar.
        if (view.Lines.Count == 0 || view.Lines.Any(l => l.Problem is CartProblem.Unavailable or CartProblem.QuantityReduced))
            return await PlacedAsync(hash, ct) ?? CheckoutResult.Changed("A disponibilidade de alguma peça mudou. Revise o carrinho antes de confirmar.");

        ShippingChoice? shipping = null;
        string? pickup = null;
        if (address is not null)
        {
            var (quote, error) = await cart.QuoteAsync(address.PostalCode, items, ct);
            if (quote is null) return CheckoutResult.Invalid(error ?? "CEP inválido.");
            var option = quote.Options.FirstOrDefault(o => o.ServiceId == request.Delivery.ServiceId);
            if (option is null)
                return CheckoutResult.Changed(quote.Options.Count == 0
                    ? quote.Message ?? "Não há entrega para este CEP."
                    : "A opção de frete escolhida não está mais disponível. Calcule o frete de novo.");
            shipping = new ShippingChoice(option.ServiceId, option.Carrier, option.Service, option.Price, option.DeliveryDays);
        }
        else
        {
            var settings = await db.StoreSettings.AsNoTracking().SingleOrDefaultAsync(ct);
            pickup = settings is { PickupEnabled: true } ? settings.PickupAddress : null;
            if (pickup is null) return CheckoutResult.Invalid("Esta loja não oferece retirada.");
        }

        if (view.Subtotal + (shipping?.Price ?? 0) != expectedTotal)
            return CheckoutResult.Changed("O total do pedido mudou. Confira os valores antes de confirmar.");

        var ids = view.Lines.Select(l => l.PartId).ToList();
        var codes = await db.Parts.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.InternalCode, ct);
        var command = new PlaceOrder(hash, buyer, address, shipping, pickup,
            view.Lines.Select(l => new PlaceOrderLine(l.PartId, codes[l.PartId], l.Title, l.UnitPrice, l.Quantity)).ToList());

        PlaceOrderResult result;
        try
        {
            result = await bus.InvokeForTenantAsync<PlaceOrderResult>(TenantId, command, ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: OrderHandler.AccessTokenIndex,
        })
        {
            // Dois envios iguais ao mesmo tempo: o outro venceu e o pedido dele é o deste comprador.
            return await PlacedAsync(hash, ct) ?? throw new InvalidOperationException("Pedido do token não encontrado.", e);
        }

        return result.Outcome == PlaceOrderOutcome.OutOfStock
            ? await PlacedAsync(hash, ct) ?? CheckoutResult.Changed("Uma das peças acabou de ser vendida. Revise o carrinho.")
            : new CheckoutResult(CheckoutOutcome.Placed, null, new PlacedOrder(result.Number!.Value, result.Total!.Value, result.PaymentDeadline!.Value));
    }

    public async Task<StoreOrderView?> FindAsync(string? token, CancellationToken ct = default)
    {
        if (HashOrNull(token) is not { } hash) return null;
        var order = await db.CustomerOrders.AsNoTracking().Include(o => o.Items).SingleOrDefaultAsync(o => o.AccessTokenHash == hash, ct);
        return order is null ? null : new StoreOrderView(order.Number, order.Status, order.PlacedAt, order.PaymentDeadline, order.BuyerName,
            order.DeliveryMethod, order.Address, order.ShippingCarrier, order.ShippingService, order.ShippingDays, order.PickupAddress,
            order.Items.Select(i => new StoreOrderItemView(i.PartId, i.Title, i.UnitPrice, i.Quantity)).ToList(),
            order.ItemsTotal, order.ShippingTotal, order.Total, order.CancelReason);
    }

    private string TenantId => (tenant.TenantId ?? throw new InvalidOperationException("Checkout sem loja no escopo.")).ToString();

    private async Task<CheckoutResult?> PlacedAsync(byte[] hash, CancellationToken ct)
    {
        var order = await db.CustomerOrders.AsNoTracking().Where(o => o.AccessTokenHash == hash)
            .Select(o => new PlacedOrder(o.Number, o.Total, o.PaymentDeadline)).SingleOrDefaultAsync(ct);
        return order is null ? null : new CheckoutResult(CheckoutOutcome.Placed, null, order);
    }

    private static byte[]? HashOrNull(string? token) =>
        token is not null && TokenFormat().IsMatch(token) ? SHA256.HashData(Encoding.ASCII.GetBytes(token)) : null;
}
