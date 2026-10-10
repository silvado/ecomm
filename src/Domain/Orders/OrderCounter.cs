using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Orders;

/// <summary>
/// Último número de pedido da loja. Incrementado por um único UPSERT na transação do pedido: a linha travada serializa
/// pedidos simultâneos da mesma loja, e um pedido desfeito devolve o número (sem buracos).
/// </summary>
public sealed class OrderCounter : ITenantOwned
{
    private OrderCounter() { }

    public Guid TenantId { get; private set; }
    public long LastNumber { get; private set; }
}
