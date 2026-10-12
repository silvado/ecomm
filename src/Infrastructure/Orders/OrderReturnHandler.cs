using System.Globalization;
using Ecommerce.Application.Inventory;
using Ecommerce.Application.Orders;
using Ecommerce.Domain.Inventory;
using Ecommerce.Domain.Orders;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Ecommerce.Infrastructure.Orders;

/// <summary>
/// Devolução de peças (RF20 CA1/CA2) na transação do Wolverine, com o pedido travado: duas devoluções ao mesmo tempo
/// são aplicadas uma de cada vez e a segunda vê o que já voltou (nunca acima do vendido). Peças que voltam ao estoque
/// entram com movimento e evento <see cref="StockChanged"/>; as avariadas ficam só registradas.
/// </summary>
public static class OrderReturnHandler
{
    private static readonly Dictionary<ReturnReason, string> Reasons = new()
    {
        [ReturnReason.Regret] = "arrependimento",
        [ReturnReason.Defect] = "defeito/garantia",
        [ReturnReason.WrongPart] = "peça errada ou não serviu",
        [ReturnReason.Other] = "outro motivo",
    };

    private static readonly Dictionary<RefundMethod, string> Methods = new()
    {
        [RefundMethod.Gateway] = "estorno no gateway",
        [RefundMethod.Pix] = "Pix",
        [RefundMethod.Cash] = "dinheiro",
        [RefundMethod.StoreCredit] = "crédito na loja",
        [RefundMethod.Other] = "outra forma",
    };

    public static async Task<(ChangeOrderResult, OutgoingMessages)> Handle(ReturnOrderItems command, TenantDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        await OrderStock.LockAsync(db, command.OrderId, ct);
        var order = await db.CustomerOrders.Include(o => o.Items).SingleOrDefaultAsync(o => o.Id == command.OrderId, ct);
        if (order is null) return (new ChangeOrderResult(ChangeOrderOutcome.NotFound, "Pedido não encontrado."), outgoing);
        var previous = await db.OrderReturns.AsNoTracking().Where(r => r.OrderId == order.Id).ToListAsync(ct);

        var now = clock.GetUtcNow();
        var from = order.Status;
        OrderReturn registered;
        try
        {
            registered = order.RegisterReturn(command.Lines ?? [], previous, command.Reason, command.Note, command.RefundAmount, command.RefundMethod, command.UserId, now);
        }
        catch (InvalidOperationException e)
        {
            return (new ChangeOrderResult(ChangeOrderOutcome.NotAllowed, $"{e.Message} Atualize a página para ver a situação atual."), outgoing);
        }
        catch (ArgumentException e)
        {
            db.ChangeTracker.Clear();
            return (new ChangeOrderResult(ChangeOrderOutcome.Invalid, e.Message.Split(" (Parameter", 2)[0]), outgoing);
        }
        db.OrderReturns.Add(registered);

        var reference = $"devolução pedido {order.Number}";
        foreach (var line in registered.Lines.Where(l => l.Restock).OrderBy(l => l.PartId))
        {
            var available = (await db.Database.SqlQuery<int>($"""
                UPDATE stocks SET on_hand = on_hand + {line.Quantity}
                WHERE part_id = {line.PartId}
                RETURNING on_hand - reserved AS "Value"
                """).ToListAsync(ct)).Single();
            db.StockMovements.Add(new StockMovement(order.TenantId, line.PartId, line.Quantity, StockMovementReason.OrderReturned, reference,
                reservationId: null, now, command.UserId));
            outgoing.Add(new StockChanged(line.PartId, available));
        }

        db.OrderHistory.Add(new OrderHistoryEntry(order.TenantId, order.Id, from, order.Status, OrderActor.User, command.UserId, Note(registered), now));
        return (new ChangeOrderResult(ChangeOrderOutcome.Done), outgoing);
    }

    /// <summary>"Devolução (defeito/garantia): 1× K-1 volta ao estoque; 1× K-2 avariada · reembolso R$ 120,00 em Pix · obs."</summary>
    private static string Note(OrderReturn r)
    {
        var lines = string.Join("; ", r.Lines.Select(l => $"{l.Quantity}× {l.InternalCode} {(l.Restock ? "volta ao estoque" : "avariada")}"));
        var refund = r.RefundAmount is { } amount
            ? $" · reembolso R$ {amount.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',')}{(r.RefundMethod is { } m ? $" em {Methods[m]}" : "")}"
            : "";
        return $"Devolução ({Reasons[r.Reason]}): {lines}{refund}{(r.Note is { } note ? $" · {note}" : "")}";
    }
}
