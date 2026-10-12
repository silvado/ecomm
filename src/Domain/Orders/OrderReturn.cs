using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Orders;

public enum ReturnReason
{
    /// <summary>Arrependimento (compra fora da loja física: 7 dias pelo CDC).</summary>
    Regret,
    /// <summary>Defeito ou garantia.</summary>
    Defect,
    /// <summary>Peça errada ou que não serviu no veículo.</summary>
    WrongPart,
    Other,
}

/// <summary>Como a loja devolveu o dinheiro (informativo: o estorno em si é feito fora do sistema — Q35).</summary>
public enum RefundMethod
{
    /// <summary>Estorno no gateway (cartão/Pix do checkout).</summary>
    Gateway,
    Pix,
    Cash,
    StoreCredit,
    Other,
}

/// <summary>Peça devolvida: <see cref="Restock"/> falso = avariada, não volta ao estoque (RF20 CA2).</summary>
public sealed record OrderReturnLine(Guid PartId, string InternalCode, string Title, int Quantity, bool Restock);

/// <summary>Pedido de devolução da loja: peça, quantidade e se volta ao estoque.</summary>
public sealed record ReturnRequestLine(Guid PartId, int Quantity, bool Restock);

/// <summary>
/// Devolução de peças de um pedido entregue ou retirado (RF20, Q35). Pode ser parcial e haver mais de uma por pedido,
/// nunca acima do vendido. O reembolso é só registrado (valor e forma); quem estorna é a loja.
/// </summary>
public sealed class OrderReturn : ITenantOwned
{
    public const int NoteMaxLength = 300;

    private OrderReturn() { }

    internal OrderReturn(CustomerOrder order, IReadOnlyList<OrderReturnLine> lines, ReturnReason reason, string? note,
        decimal? refundAmount, RefundMethod? refundMethod, Guid userId, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7();
        TenantId = order.TenantId;
        OrderId = order.Id;
        Lines = lines.ToList();
        Reason = reason;
        Note = note;
        RefundAmount = refundAmount;
        RefundMethod = refundMethod;
        UserId = userId;
        At = now;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrderId { get; private set; }
    public List<OrderReturnLine> Lines { get; private set; } = [];
    public ReturnReason Reason { get; private set; }
    public string? Note { get; private set; }
    public decimal? RefundAmount { get; private set; }
    public RefundMethod? RefundMethod { get; private set; }
    public Guid UserId { get; private set; }
    public DateTimeOffset At { get; private set; }
}
