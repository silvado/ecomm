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
/// Venda de balcão (RF18, RNF02). Na transação do Wolverine: cada peça é baixada por UPDATE condicional (todas ou
/// nenhuma), e o pedido, as reservas convertidas, os movimentos, o histórico e os eventos <see cref="StockChanged"/> são
/// confirmados juntos. O mesmo formulário enviado duas vezes esbarra no índice único do hash e nada é gravado.
/// </summary>
public static class CounterSaleHandler
{
    private static readonly Dictionary<CounterPaymentMethod, string> PaymentLabels = new()
    {
        [CounterPaymentMethod.Cash] = "dinheiro",
        [CounterPaymentMethod.Pix] = "Pix",
        [CounterPaymentMethod.DebitCard] = "cartão de débito",
        [CounterPaymentMethod.CreditCard] = "cartão de crédito",
        [CounterPaymentMethod.Other] = "outra forma",
    };

    private sealed record PartRow(Guid Id, string InternalCode, string Title, decimal Price);

    public static async Task<(CounterSaleResult, OutgoingMessages)> Handle(RegisterCounterSale command, TenantDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        var lines = command.Lines ?? [];
        if (lines.Count is 0 or > CustomerOrder.MaxLines) return (Invalid($"A venda precisa ter de 1 a {CustomerOrder.MaxLines} peças."), outgoing);
        if (lines.Select(l => l.PartId).Distinct().Count() != lines.Count) return (Invalid("Peça repetida na venda: ajuste a quantidade."), outgoing);
        if (lines.Any(l => l.Quantity <= 0)) return (Invalid("Quantidade precisa ser positiva."), outgoing);
        if (lines.Any(l => l.UnitPrice <= 0 || decimal.Round(l.UnitPrice, 2) != l.UnitPrice || l.UnitPrice > 9_999_999_999.99m))
            return (Invalid("Preço inválido: use valor positivo com até 2 casas decimais."), outgoing);
        if (!Enum.IsDefined(command.PaymentMethod)) return (Invalid("Escolha a forma de pagamento."), outgoing);
        try
        {
            CustomerOrder.NormalizeCounterCustomer(command.Customer);
        }
        catch (ArgumentException e)
        {
            return (Invalid(e.Message.Split(" (Parameter", 2)[0]), outgoing);
        }

        // Reenvio do mesmo formulário depois de a venda entrar: devolve a venda, mesmo que o estoque já tenha acabado
        // (por ela). Dois envios simultâneos caem no índice único do hash, tratado por quem chamou.
        var existing = await db.CustomerOrders.AsNoTracking().Where(o => o.AccessTokenHash == command.IdempotencyHash)
            .Select(o => new { o.Id, o.Number }).SingleOrDefaultAsync(ct);
        if (existing is not null) return (new CounterSaleResult(CounterSaleOutcome.AlreadyRegistered, existing.Id, existing.Number), outgoing);

        var ids = lines.Select(l => l.PartId).ToList();
        var parts = await db.Parts.AsNoTracking().Where(p => ids.Contains(p.Id))
            .Select(p => new PartRow(p.Id, p.InternalCode, p.Title, p.Price)).ToDictionaryAsync(p => p.Id, ct);
        if (parts.Count != ids.Count) return (Invalid("Peça não encontrada. Atualize a página."), outgoing);

        var tenantId = db.RequireTenantId();
        var now = clock.GetUtcNow();

        // Todas as peças ou nenhuma, em ordem fixa por peça (vendas concorrentes travam as linhas na mesma ordem).
        var sold = new List<(CounterSaleLine Line, int Available)>();
        foreach (var line in lines.OrderBy(l => l.PartId))
        {
            var available = (await db.Database.SqlQuery<int>($"""
                UPDATE stocks SET on_hand = on_hand - {line.Quantity}
                WHERE part_id = {line.PartId} AND on_hand - reserved >= {line.Quantity}
                RETURNING on_hand - reserved AS "Value"
                """).ToListAsync(ct)).Cast<int?>().SingleOrDefault();
            if (available is null)
            {
                foreach (var (done, _) in sold)
                    await db.Database.ExecuteSqlAsync($"UPDATE stocks SET on_hand = on_hand + {done.Quantity} WHERE part_id = {done.PartId}", ct);
                var part = parts[line.PartId];
                return (new CounterSaleResult(CounterSaleOutcome.OutOfStock,
                    Message: $"Sem estoque disponível de {part.InternalCode} · {part.Title} para {line.Quantity} unidade(s)."), outgoing);
            }
            sold.Add((line, available.Value));
        }

        var number = (await db.Database.SqlQuery<long>($"""
            INSERT INTO order_counters (tenant_id, last_number) VALUES ({tenantId}, 1)
            ON CONFLICT (tenant_id) DO UPDATE SET last_number = order_counters.last_number + 1
            RETURNING last_number AS "Value"
            """).ToListAsync(ct)).Single();

        var reference = $"pedido {number}";
        var orderLines = new List<OrderLine>();
        foreach (var (line, available) in sold)
        {
            var part = parts[line.PartId];
            var reservation = StockReservation.Sold(tenantId, line.PartId, line.Quantity, reference, now);
            db.StockReservations.Add(reservation);
            db.StockMovements.Add(new StockMovement(tenantId, line.PartId, -line.Quantity, StockMovementReason.DirectSale, reference,
                reservation.Id, now, command.UserId));
            orderLines.Add(new OrderLine(line.PartId, part.InternalCode, part.Title, line.UnitPrice, line.Quantity, reservation.Id));
            outgoing.Add(new StockChanged(line.PartId, available));
        }

        var order = CustomerOrder.PlaceCounterSale(tenantId, number, command.IdempotencyHash, command.Customer, command.PaymentMethod,
            lines.Select(l => orderLines.Single(o => o.PartId == l.PartId)).ToList(), now);
        db.CustomerOrders.Add(order);
        db.OrderHistory.Add(new OrderHistoryEntry(tenantId, order.Id, null, order.Status, OrderActor.User, command.UserId,
            Note(command.PaymentMethod, lines, parts), now));
        return (new CounterSaleResult(CounterSaleOutcome.Registered, order.Id, order.Number), outgoing);
    }

    /// <summary>"Venda de balcão · pago em Pix · preço ajustado: K-1 de R$ 120,00 por R$ 100,00".</summary>
    private static string Note(CounterPaymentMethod method, IReadOnlyList<CounterSaleLine> lines, Dictionary<Guid, PartRow> parts)
    {
        var note = $"Venda de balcão · pago em {PaymentLabels[method]}";
        var adjusted = lines.Where(l => l.UnitPrice != parts[l.PartId].Price)
            .Select(l => $"{parts[l.PartId].InternalCode} de {Money(parts[l.PartId].Price)} por {Money(l.UnitPrice)}")
            .ToList();
        return adjusted.Count == 0 ? note : $"{note} · preço ajustado: {string.Join("; ", adjusted)}";
    }

    // Sem depender da cultura instalada na imagem: R$ 1234,50.
    private static string Money(decimal value) => "R$ " + value.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');

    private static CounterSaleResult Invalid(string message) => new(CounterSaleOutcome.Invalid, Message: message);
}
