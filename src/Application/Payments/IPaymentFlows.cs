namespace Ecommerce.Application.Payments;

public enum StartPaymentOutcome
{
    Redirect,
    NotFound,
    /// <summary>O pedido não aguarda pagamento (pago, cancelado, em análise) ou o prazo acabou.</summary>
    NotPayable,
    /// <summary>A loja não recebe pagamento online (sem gateway configurado).</summary>
    Unavailable,
}

public sealed record StartPaymentResult(StartPaymentOutcome Outcome, string? RedirectUrl, string? Message);

/// <summary>Leva o comprador da página do pedido ao checkout do gateway da loja do Host (RF15).</summary>
public interface IStorefrontPayment
{
    /// <param name="storeOrigin">Origem da loja (https://host) para o gateway devolver o comprador.</param>
    Task<StartPaymentResult> StartAsync(string? token, string storeOrigin, CancellationToken ct = default);
}

public enum WebhookOutcome
{
    Accepted,
    /// <summary>Válida, mas sem efeito (outro tópico ou reentrega).</summary>
    Ignored,
    /// <summary>Assinatura inválida.</summary>
    Rejected,
    /// <summary>Loja ou gateway desconhecido.</summary>
    NotFound,
}

/// <summary>Recebe o webhook de pagamento: confere a assinatura, grava e devolve logo (regra 7 do CLAUDE.md).</summary>
public interface IPaymentWebhook
{
    Task<WebhookOutcome> ReceiveAsync(Guid tenantId, string gateway, WebhookRequest request, CancellationToken ct = default);
}
