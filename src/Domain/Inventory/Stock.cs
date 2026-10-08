using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Inventory;

/// <summary>
/// Saldo de uma peça. <b>Nunca</b> alterar por "ler, calcular e gravar": reservas e baixas são UPDATEs condicionais
/// no banco (RNF02). As constraints CHECK da tabela são a última barreira contra saldo negativo.
/// </summary>
public sealed class Stock : ITenantOwned
{
    private Stock() { }

    public Stock(Guid tenantId, Guid partId, int onHand)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant obrigatório.", nameof(tenantId));
        ArgumentOutOfRangeException.ThrowIfNegative(onHand);
        TenantId = tenantId;
        PartId = partId;
        OnHand = onHand;
    }

    public Guid PartId { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>Unidades fisicamente na loja.</summary>
    public int OnHand { get; private set; }

    /// <summary>Unidades seguras por pedidos ainda não pagos.</summary>
    public int Reserved { get; private set; }

    public int Available => OnHand - Reserved;
}
