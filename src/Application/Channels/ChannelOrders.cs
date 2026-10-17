namespace Ecommerce.Application.Channels;

/// <summary>Item vendido no canal: o anúncio de lá, quantidade e preço pago por unidade.</summary>
public sealed record ChannelOrderItem(string ExternalItemId, string Title, int Quantity, decimal UnitPrice);

/// <summary>
/// Pedido como o canal o mostra agora (consultado na API depois do aviso — o aviso nunca é confiado). <see cref="Status"/>
/// é o do canal: no ML, <c>paid</c>, <c>cancelled</c>, <c>invalid</c>, <c>confirmed</c>...
/// </summary>
public sealed record ChannelOrderSnapshot(
    string ExternalId, string Status, string? BuyerName, decimal ShippingCost, DateTimeOffset? PaidAt, IReadOnlyList<ChannelOrderItem> Items);

/// <summary>Importa (ou atualiza) a venda de um canal na loja do escopo (RF21, RF17 CA2). Uma por aviso de pedido.</summary>
public sealed record ImportChannelOrder(Guid ConnectionId, ChannelOrderSnapshot Order);

public enum ChannelOrderOutcome
{
    /// <summary>Pedido criado e peças baixadas.</summary>
    Created,
    /// <summary>Criado em análise: peça sem saldo (conflito de estoque — Q01) ou anúncio que não é da plataforma.</summary>
    CreatedForReview,
    /// <summary>Cancelado no canal: cancelado aqui e peças devolvidas ao estoque.</summary>
    Canceled,
    /// <summary>Já importado e nada mudou (aviso repetido, outra mudança no canal).</summary>
    Unchanged,
    /// <summary>Ainda não pago, nenhum item é anúncio da plataforma, ou cancelado antes de ser importado.</summary>
    Ignored,
}

public sealed record ChannelOrderResult(ChannelOrderOutcome Outcome, Guid? OrderId = null, long? Number = null);
