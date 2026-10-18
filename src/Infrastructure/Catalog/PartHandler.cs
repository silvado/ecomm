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
        var oldPrice = part.Price;
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
        }
        // Os canais leem saldo e preço atuais a cada StockChanged: preço mudado também precisa avisar (RF24; Meta RF22 CA2).
        if (delta != 0 || part.Price != oldPrice) outgoing.Add(new StockChanged(part.Id, rows[0].Available));
        return (PartCommandResult.Ok(part.Id), outgoing);
    }

    public static async Task<(PartCommandResult, OutgoingMessages)> Handle(ChangePartStatus command, TenantDbContext db, Platform.PlanDirectory plans,
        TimeProvider clock, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        var part = await db.Parts.Include(p => p.Photos).SingleOrDefaultAsync(p => p.Id == command.PartId, ct);
        if (part is null) return (PartCommandResult.Fail(PartCommandOutcome.NotFound), outgoing);
        var before = part.Status;

        try
        {
            switch (command.Status)
            {
                case PartStatus.Active:
                    // RF10 CA2: no modo desmontadora, peça usada só vai à venda com a origem completa.
                    if (await MissingForSaleAsync(db, part, part.DonorVehicleId, ct) is { Count: > 0 } missing) return (Invalid(Traceability.Message(missing)), outgoing);
                    if (before != PartStatus.Active && await ActivePartsLimitAsync(db, plans, ct) is { } limitMessage) return (Invalid(limitMessage), outgoing);
                    part.Activate(clock.GetUtcNow());
                    break;
                case PartStatus.Inactive: part.Deactivate(clock.GetUtcNow()); break;
                default: return (Invalid("Uma peça não volta para rascunho."), outgoing);
            }
        }
        catch (InvalidOperationException e)
        {
            return (Invalid(e.Message), outgoing);
        }
        // Ativar ou inativar muda o que os canais podem vender (inativa = anúncio pausado — RF24).
        if (part.Status != before)
        {
            var available = await db.Stocks.Where(s => s.PartId == part.Id).Select(s => s.OnHand - s.Reserved).SingleOrDefaultAsync(ct);
            outgoing.Add(new StockChanged(part.Id, available));
        }
        return (PartCommandResult.Ok(part.Id), outgoing);
    }

    /// <summary>
    /// RF37: peças ativas no limite do plano. A trava por loja (até o fim da transação) faz duas ativações simultâneas
    /// contarem uma depois da outra. Nulo = pode ativar.
    /// </summary>
    private static async Task<string?> ActivePartsLimitAsync(TenantDbContext db, Platform.PlanDirectory plans, CancellationToken ct)
    {
        var tenantId = db.RequireTenantId();
        var plan = await plans.ForTenantAsync(tenantId, ct);
        if (plan.Limit(Domain.Platform.PlanLimit.ActiveParts) is not { } limit) return null;
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended('active_parts:' || {tenantId.ToString()}, 0))", ct);
        var active = await db.Parts.CountAsync(p => p.Status == PartStatus.Active, ct);
        return active >= limit ? Platform.PlanDirectory.LimitMessage(plan, "peças ativas", limit) : null;
    }
    public const string TraceabilityTagIndex = "ix_parts_tenant_id_traceability_tag";
    public const string TagTakenMessage = "Esta etiqueta de rastreabilidade já está em outra peça da loja.";

    /// <summary>
    /// Origem da peça (RF10). O veículo precisa ser da loja (o filtro e o RLS escondem os das outras) e os campos, os que a
    /// loja configurou. Peça à venda no modo desmontadora não pode ficar sem o que a lei exige.
    /// </summary>
    public static async Task<PartCommandResult> Handle(SetPartTraceability command, TenantDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var part = await db.Parts.SingleOrDefaultAsync(p => p.Id == command.PartId, ct);
        if (part is null) return PartCommandResult.Fail(PartCommandOutcome.NotFound);
        if (command.DonorVehicleId is { } donorId && !await db.DonorVehicles.AnyAsync(d => d.Id == donorId, ct))
            return Invalid("Veículo doador não encontrado.");

        var settings = await db.StoreSettings.AsNoTracking().SingleOrDefaultAsync(ct);
        var keys = (settings?.TraceabilityFields ?? []).Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        if (command.CustomFields?.Keys.FirstOrDefault(k => !keys.Contains(k)) is { } unknown)
            return Invalid($"Campo de rastreabilidade desconhecido: {unknown}. Atualize a página.");

        try { part.SetTraceability(command.DonorVehicleId, command.Tag, command.CustomFields, clock.GetUtcNow()); }
        catch (ArgumentException e) { return Invalid(Message(e)); }

        // As conferências abaixo olham a peça já alterada; recusada, a alteração é descartada (a transação do Wolverine
        // salvaria o que ficou rastreado).
        if (part.TraceabilityTag is { } tag && await db.Parts.AnyAsync(p => p.TraceabilityTag == tag && p.Id != part.Id, ct))
            return Discard(db, PartCommandResult.Fail(PartCommandOutcome.CodeTaken, TagTakenMessage));
        if (part.Status == PartStatus.Active && await MissingForSaleAsync(db, part, part.DonorVehicleId, ct) is { Count: > 0 } missing)
            return Discard(db, Invalid($"{Traceability.Message(missing)} Para tirar esses dados, inative a peça antes."));
        return PartCommandResult.Ok(part.Id);
    }

    private static PartCommandResult Discard(TenantDbContext db, PartCommandResult result)
    {
        db.ChangeTracker.Clear();
        return result;
    }

    private static async Task<IReadOnlyList<string>> MissingForSaleAsync(TenantDbContext db, Part part, Guid? donorId, CancellationToken ct)
    {
        var settings = await db.StoreSettings.AsNoTracking().SingleOrDefaultAsync(ct);
        if (settings is not { DismantlerMode: true }) return [];
        var donor = donorId is { } id ? await db.DonorVehicles.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct) : null;
        return Traceability.MissingForSale(true, settings.TraceabilityFields, part, donor);
    }

    public const string CodeTakenMessage = "Já existe uma peça com este código interno.";

    private static PartCommandResult Invalid(string message) => PartCommandResult.Fail(PartCommandOutcome.Invalid, message);

    private static string Message(ArgumentException e) => e.Message.Split(" (Parameter", 2)[0];
}
