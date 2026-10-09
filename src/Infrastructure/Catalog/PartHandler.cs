using Ecommerce.Application.Catalog;
using Ecommerce.Application.Inventory;
using Ecommerce.Domain.Catalog;
using Ecommerce.Domain.Inventory;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Ecommerce.Infrastructure.Catalog;

/// <summary>
/// Cadastro de peça (RF08). Roda na transação do Wolverine (AutoApplyTransactions): peça, saldo, movimento e o evento
/// <see cref="StockChanged"/> são confirmados juntos. A quantidade só muda por UPDATE condicional (RNF02).
/// O código interno repetido em corrida é barrado pelo índice único — o chamador recebe a exceção e responde 409.
/// </summary>
public static class PartHandler
{
    public const string InternalCodeIndex = "ix_parts_tenant_id_internal_code";

    // Colunas em snake_case: a convenção de nomes do EF também vale para SqlQuery de tipos não mapeados.
    private sealed record AdjustedRow(int OldOnHand, int Available);

    public static async Task<(PartCommandResult, OutgoingMessages)> Handle(
        CreatePart command, TenantDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        if (command.Quantity is < 0 or > PartCommandResult.MaxQuantity)
            return (Invalid($"Quantidade precisa estar entre 0 e {PartCommandResult.MaxQuantity}."), outgoing);

        var tenantId = db.RequireTenantId();
        var now = clock.GetUtcNow();
        Part part;
        try { part = Part.Create(tenantId, command.InternalCode, command.Details, now); }
        catch (ArgumentException e) { return (Invalid(Message(e)), outgoing); }

        if (await db.Parts.AnyAsync(p => p.InternalCode == part.InternalCode, ct))
            return (PartCommandResult.Fail(PartCommandOutcome.CodeTaken, CodeTakenMessage), outgoing);

        db.Parts.Add(part);
        db.Stocks.Add(new Stock(tenantId, part.Id, command.Quantity));
        if (command.Quantity > 0)
            db.StockMovements.Add(new StockMovement(tenantId, part.Id, command.Quantity, StockMovementReason.Adjustment,
                "cadastro", reservationId: null, now, command.UserId));
        outgoing.Add(new StockChanged(part.Id, command.Quantity));
        return (PartCommandResult.Ok(part.Id), outgoing);
    }

    public static async Task<(PartCommandResult, OutgoingMessages)> Handle(
        UpdatePart command, TenantDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        if (command.Quantity is < 0 or > PartCommandResult.MaxQuantity)
            return (Invalid($"Quantidade precisa estar entre 0 e {PartCommandResult.MaxQuantity}."), outgoing);

        var part = await db.Parts.Include(p => p.OemCodes).SingleOrDefaultAsync(p => p.Id == command.PartId, ct);
        if (part is null) return (PartCommandResult.Fail(PartCommandOutcome.NotFound), outgoing);

        var now = clock.GetUtcNow();
        try { part.Update(command.Details, now); }
        catch (ArgumentException e) { return (Invalid(Message(e)), outgoing); }

        // Define o saldo físico num único comando: nunca abaixo do reservado, mesmo com reservas chegando agora.
        // A subconsulta trava a linha e lê o saldo anterior (para o movimento) antes do UPDATE; com uma reserva
        // concorrente, o PostgreSQL reavalia "reserved <= quantidade" sobre a versão mais nova da linha.
        var rows = await db.Database.SqlQuery<AdjustedRow>($"""
            UPDATE stocks s SET on_hand = {command.Quantity}
            FROM (SELECT part_id, on_hand FROM stocks WHERE part_id = {part.Id} FOR UPDATE) old
            WHERE s.part_id = old.part_id AND s.reserved <= {command.Quantity}
            RETURNING old.on_hand AS old_on_hand, s.on_hand - s.reserved AS available
            """).ToListAsync(ct);
        if (rows.Count == 0)
        {
            // Nada é gravado: a transação do Wolverine só persiste o que estiver no rastreador.
            db.ChangeTracker.Clear();
            var reserved = await db.Stocks.Where(s => s.PartId == command.PartId).Select(s => s.Reserved).SingleAsync(ct);
            return (PartCommandResult.Fail(PartCommandOutcome.BelowReserved,
                $"Há {reserved} unidade(s) reservada(s) para pedidos em pagamento: a quantidade não pode ficar abaixo disso."), outgoing);
        }

        var delta = command.Quantity - rows[0].OldOnHand;
        if (delta != 0)
        {
            db.StockMovements.Add(new StockMovement(part.TenantId, part.Id, delta, StockMovementReason.Adjustment,
                "edicao", reservationId: null, now, command.UserId));
            outgoing.Add(new StockChanged(part.Id, rows[0].Available));
        }
        return (PartCommandResult.Ok(part.Id), outgoing);
    }

    public static async Task<PartCommandResult> Handle(ChangePartStatus command, TenantDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var part = await db.Parts.SingleOrDefaultAsync(p => p.Id == command.PartId, ct);
        if (part is null) return PartCommandResult.Fail(PartCommandOutcome.NotFound);

        switch (command.Status)
        {
            case PartStatus.Active: part.Activate(clock.GetUtcNow()); break;
            case PartStatus.Inactive: part.Deactivate(clock.GetUtcNow()); break;
            default: return Invalid("Uma peça não volta para rascunho.");
        }
        return PartCommandResult.Ok(part.Id);
    }

    public const string CodeTakenMessage = "Já existe uma peça com este código interno.";

    private static PartCommandResult Invalid(string message) => PartCommandResult.Fail(PartCommandOutcome.Invalid, message);

    private static string Message(ArgumentException e) => e.Message.Split(" (Parameter", 2)[0];
}
