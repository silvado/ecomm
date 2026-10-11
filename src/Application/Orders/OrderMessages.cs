using Ecommerce.Domain.Orders;

namespace Ecommerce.Application.Orders;

// Contratos do pedido da loja (RF14, RF12). Enviados com o tenant no envelope (IMessageBus.InvokeForTenantAsync);
// o handler fica na Infrastructure, junto do SQL atômico de reserva.

/// <summary>Item com o preço e o título já conferidos pelo servidor.</summary>
public sealed record PlaceOrderLine(Guid PartId, string InternalCode, string Title, decimal UnitPrice, int Quantity);

/// <summary>
/// Cria o pedido aguardando pagamento e reserva todas as peças na mesma transação — ou todas, ou nenhuma.
/// Sem <paramref name="Shipping"/>, é retirada em <paramref name="PickupAddress"/>.
/// </summary>
public sealed record PlaceOrder(byte[] AccessTokenHash, BuyerDetails Buyer, DeliveryAddress? Address, ShippingChoice? Shipping,
    string? PickupAddress, IReadOnlyList<PlaceOrderLine> Lines);

public enum PlaceOrderOutcome
{
    Placed,
    /// <summary>Alguma peça ficou sem saldo entre o carrinho e o envio; nada foi reservado.</summary>
    OutOfStock,
}

public sealed record PlaceOrderResult(PlaceOrderOutcome Outcome, long? Number, decimal? Total, DateTimeOffset? PaymentDeadline, Guid? UnavailablePartId)
{
    public static PlaceOrderResult Unavailable(Guid partId) => new(PlaceOrderOutcome.OutOfStock, null, null, null, partId);
}

/// <summary>Linha da venda de balcão: o preço praticado pode ser diferente do cadastro (desconto negociado, Q32).</summary>
public sealed record CounterSaleLine(Guid PartId, int Quantity, decimal UnitPrice);

/// <summary>
/// Venda de balcão (RF18): baixa todas as peças ou nenhuma e cria o pedido já pago e retirado. O hash do token do
/// formulário evita lançar a mesma venda duas vezes (duplo clique).
/// </summary>
public sealed record RegisterCounterSale(byte[] IdempotencyHash, Guid UserId, CounterCustomer? Customer, CounterPaymentMethod PaymentMethod,
    IReadOnlyList<CounterSaleLine> Lines);

public enum CounterSaleOutcome
{
    Registered,
    /// <summary>O mesmo formulário já tinha sido registrado: nada novo foi baixado.</summary>
    AlreadyRegistered,
    /// <summary>Dado inválido (cliente, preço, quantidade, peça inativa ou inexistente).</summary>
    Invalid,
    /// <summary>Alguma peça não tem saldo; nada foi baixado.</summary>
    OutOfStock,
}

public sealed record CounterSaleResult(CounterSaleOutcome Outcome, Guid? OrderId = null, long? Number = null, string? Message = null);

/// <summary>Agendada para o fim do prazo de pagamento; sem efeito se o pedido já foi pago ou cancelado.</summary>
public sealed record ExpireOrder(Guid OrderId);

/// <summary>Ações da loja sobre o pedido no painel (RF17).</summary>
public enum OrderAction
{
    /// <summary>Confirma a venda em análise: baixa as peças que ainda não foram baixadas.</summary>
    Approve,
    /// <summary>Cancela (motivo obrigatório): libera reservas e devolve ao estoque o que já foi baixado.</summary>
    Cancel,
    Ship,
    Deliver,
    PickUp,
}

/// <param name="Note">Motivo do cancelamento.</param>
/// <param name="TrackingCode">Rastreio, ao marcar como enviado (opcional).</param>
public sealed record ChangeOrder(Guid OrderId, OrderAction Action, Guid UserId, string? Note = null, string? TrackingCode = null);

public enum ChangeOrderOutcome
{
    Done,
    NotFound,
    /// <summary>A ação não vale para a situação atual (ex.: outra pessoa já mudou o pedido).</summary>
    NotAllowed,
    /// <summary>Dado inválido (motivo vazio, rastreio longo).</summary>
    Invalid,
    /// <summary>Aprovação recusada: alguma peça não tem mais saldo.</summary>
    OutOfStock,
}

public sealed record ChangeOrderResult(ChangeOrderOutcome Outcome, string? Message = null);
