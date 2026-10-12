using Ecommerce.Application.Email;
using Ecommerce.Application.Orders;
using Ecommerce.Domain.Orders;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Ecommerce.Infrastructure.Orders;

/// <summary>
/// Ações da loja sobre o pedido no painel (RF17). Na transação do Wolverine, com a linha do pedido travada: dois cliques,
/// duas pessoas ou um pagamento chegando ao mesmo tempo são aplicados um de cada vez, e o segundo vê a situação nova
/// (recebe <see cref="ChangeOrderOutcome.NotAllowed"/> se a ação deixou de valer). Estoque e histórico na mesma transação.
/// </summary>
public static class OrderActionsHandler
{
    public static async Task<(ChangeOrderResult, OutgoingMessages)> Handle(ChangeOrder command, TenantDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        await OrderStock.LockAsync(db, command.OrderId, ct);
        var order = await db.CustomerOrders.Include(o => o.Items).SingleOrDefaultAsync(o => o.Id == command.OrderId, ct);
        if (order is null) return (new ChangeOrderResult(ChangeOrderOutcome.NotFound, "Pedido não encontrado."), outgoing);

        var now = clock.GetUtcNow();
        var from = order.Status;
        string? note = null;
        try
        {
            switch (command.Action)
            {
                case OrderAction.Approve:
                    if (from != OrderStatus.NeedsReview) throw new InvalidOperationException("O pedido não está em análise.");
                    var (missing, events) = await OrderStock.ConfirmAllAsync(db, order, now, ct);
                    if (missing is not null)
                        return (new ChangeOrderResult(ChangeOrderOutcome.OutOfStock,
                            $"Sem estoque de \"{missing.Title}\" para aprovar a venda. Cancele o pedido ou ajuste o estoque."), outgoing);
                    outgoing.AddRange(events);
                    note = order.ReviewReason;
                    order.ApproveReview(now);
                    outgoing.Add(new NotifyBuyer(order.Id, OrderEmail.Paid));
                    break;

                case OrderAction.Cancel:
                    order.CancelByStore(command.Note ?? string.Empty, now);
                    outgoing.AddRange(await OrderStock.ReturnAllAsync(db, order, command.UserId, now, ct));
                    note = order.CancelReason;
                    outgoing.Add(new NotifyBuyer(order.Id, OrderEmail.Canceled));
                    break;

                case OrderAction.Ship:
                    order.MarkShipped(command.TrackingCode, now);
                    note = order.TrackingCode is { } code ? $"Rastreio: {code}" : null;
                    outgoing.Add(new NotifyBuyer(order.Id, OrderEmail.Shipped));
                    break;

                case OrderAction.Deliver:
                    order.MarkDelivered(now);
                    break;

                case OrderAction.PickUp:
                    order.MarkPickedUp(now);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(command), command.Action, null);
            }
        }
        catch (InvalidOperationException e)
        {
            return (new ChangeOrderResult(ChangeOrderOutcome.NotAllowed, $"{e.Message} Atualize a página para ver a situação atual."), outgoing);
        }
        catch (ArgumentException e)
        {
            return (new ChangeOrderResult(ChangeOrderOutcome.Invalid, e.Message.Split(" (Parameter", 2)[0]), outgoing);
        }

        db.OrderHistory.Add(new OrderHistoryEntry(order.TenantId, order.Id, from, order.Status, OrderActor.User, command.UserId, note, now));
        return (new ChangeOrderResult(ChangeOrderOutcome.Done), outgoing);
    }
}
