using Ecommerce.Domain.Catalog;

namespace Ecommerce.Application.Catalog;

// Comandos do catálogo (RF08). Enviados com o tenant no envelope (IMessageBus.InvokeForTenantAsync): peça, estoque,
// movimento e o evento StockChanged são confirmados juntos pela transação do Wolverine (outbox).

/// <summary>Cria a peça em rascunho com o saldo inicial. <paramref name="UserId"/> vai para o movimento de estoque.</summary>
public sealed record CreatePart(string InternalCode, PartDetails Details, int Quantity, Guid UserId);

/// <summary>Altera os dados e define a quantidade física (ajuste atômico: nunca abaixo do reservado).</summary>
public sealed record UpdatePart(Guid PartId, PartDetails Details, int Quantity, Guid UserId);

public sealed record ChangePartStatus(Guid PartId, PartStatus Status);

public enum PartCommandOutcome
{
    Done,
    NotFound,
    /// <summary>Código interno já usado por outra peça desta loja (RF08 CA2).</summary>
    CodeTaken,
    /// <summary>A nova quantidade é menor que o que está reservado para pedidos em pagamento.</summary>
    BelowReserved,
    Invalid,
}

/// <summary><see cref="Message"/> vem pronta para o usuário quando o resultado não é <see cref="PartCommandOutcome.Done"/>.</summary>
public sealed record PartCommandResult(PartCommandOutcome Outcome, Guid? PartId = null, string? Message = null)
{
    public const int MaxQuantity = 1_000_000;

    public static PartCommandResult Ok(Guid partId) => new(PartCommandOutcome.Done, partId);
    public static PartCommandResult Fail(PartCommandOutcome outcome, string? message = null) => new(outcome, null, message);
}
