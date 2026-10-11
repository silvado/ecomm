using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Orders;

/// <summary>Quem mudou o pedido: o comprador (ao comprar), um usuário da loja, o gateway de pagamento ou o sistema (prazo).</summary>
public enum OrderActor
{
    Buyer,
    User,
    Payment,
    System,
}

/// <summary>Linha do tempo do pedido (RF17): cada mudança de situação, com quem fez e a observação (motivo, rastreio).</summary>
public sealed class OrderHistoryEntry : ITenantOwned
{
    public const int NoteMaxLength = 300;

    private OrderHistoryEntry() { }

    public OrderHistoryEntry(Guid tenantId, Guid orderId, OrderStatus? from, OrderStatus to, OrderActor actor, Guid? userId, string? note, DateTimeOffset at)
    {
        if (actor == OrderActor.User && userId is null) throw new ArgumentException("Mudança feita por usuário precisa do usuário.", nameof(userId));
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        OrderId = orderId;
        FromStatus = from;
        ToStatus = to;
        Actor = actor;
        UserId = userId;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Length > NoteMaxLength ? note[..NoteMaxLength] : note;
        At = at;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrderId { get; private set; }

    /// <summary>Nulo na criação do pedido.</summary>
    public OrderStatus? FromStatus { get; private set; }

    public OrderStatus ToStatus { get; private set; }
    public OrderActor Actor { get; private set; }
    public Guid? UserId { get; private set; }
    public string? Note { get; private set; }
    public DateTimeOffset At { get; private set; }
}
