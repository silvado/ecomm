using Ecommerce.Domain.Payments;

namespace Ecommerce.Application.Payments;

public sealed record PaymentCheckoutItem(string Title, int Quantity, decimal UnitPrice);

/// <param name="ExpiresAt">Fim da reserva: depois disso o gateway não deve aceitar o pagamento.</param>
/// <param name="ReturnUrl">Para onde o comprador volta (sucesso, pendente ou desistência) — a página do pedido.</param>
/// <param name="NotificationUrl">Webhook desta loja na plataforma.</param>
public sealed record PaymentCheckoutRequest(
    Guid OrderId, long OrderNumber, IReadOnlyList<PaymentCheckoutItem> Items, decimal ShippingTotal, decimal Total,
    string BuyerName, string BuyerEmail, DateTimeOffset ExpiresAt, string ReturnUrl, string NotificationUrl);

/// <summary>Página de pagamento do gateway para onde o comprador é levado (dados de cartão nunca passam por aqui — RF15 CA2).</summary>
public sealed record PaymentCheckout(string RedirectUrl);

/// <summary>Requisição do webhook como chegou, para o adaptador conferir a assinatura no formato do seu gateway.</summary>
public sealed record WebhookRequest(IReadOnlyDictionary<string, string> Headers, IReadOnlyDictionary<string, string> Query, string Body);

public enum NotificationValidity
{
    /// <summary>Assinatura confere e é sobre um pagamento.</summary>
    Payment,
    /// <summary>Assinatura confere, mas não é sobre pagamento (ex.: outro tópico): responde 2xx e ignora.</summary>
    Other,
    /// <summary>Assinatura ausente ou errada: recusada sem gravar nada.</summary>
    Invalid,
}

/// <param name="IdempotencyKey">Igual em reentregas da mesma notificação.</param>
/// <param name="PaymentId">Id do pagamento no gateway, a consultar.</param>
public sealed record PaymentNotification(NotificationValidity Validity, string? IdempotencyKey, string? PaymentId)
{
    public static readonly PaymentNotification Invalid = new(NotificationValidity.Invalid, null, null);
    public static readonly PaymentNotification Other = new(NotificationValidity.Other, null, null);
}

/// <param name="OrderReference">Referência que enviamos ao criar o checkout (id do pedido).</param>
public sealed record GatewayPayment(string ExternalId, PaymentStatus Status, decimal Amount, string? OrderReference, string? Method);

/// <summary>
/// Referência do pedido enviada ao gateway: o id do pedido ou, quando o gateway só aceita um número curto (Cielo: letras e
/// dígitos, até 20), <c>PED{número}</c> — único na loja.
/// </summary>
public static class OrderReference
{
    private const string NumberPrefix = "PED";

    public static string FromNumber(long number) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{NumberPrefix}{number}");

    public static long? NumberOf(string? reference) =>
        reference is { Length: > 3 and <= 20 } && reference.StartsWith(NumberPrefix, StringComparison.Ordinal)
        && long.TryParse(reference.AsSpan(NumberPrefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
}

public sealed class PaymentGatewayException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Gateway de pagamento de uma loja (RF15), com as credenciais dela. Implementações: fake (dev e testes) e Mercado Pago;
/// Cielo depois. Mensagens de exceção nunca levam credenciais.
/// </summary>
public interface IPaymentGateway
{
    /// <summary>Nome estável usado na URL do webhook e nos registros (ex.: <c>mercado-pago</c>).</summary>
    string Name { get; }

    Task<PaymentCheckout> CreateCheckoutAsync(PaymentCheckoutRequest request, CancellationToken ct = default);

    /// <summary>Confere a assinatura do webhook. Nada além do id do pagamento é aproveitado da notificação.</summary>
    Task<PaymentNotification> ReadNotificationAsync(WebhookRequest request, CancellationToken ct = default);

    /// <summary>Estado verdadeiro do pagamento, consultado no gateway; nulo se ele não existe para esta conta.</summary>
    Task<GatewayPayment?> GetPaymentAsync(string paymentId, CancellationToken ct = default);
}

/// <summary>Gateway da loja do escopo: o configurado no cofre ou, em dev, o fake. Nulo se a loja não recebe online.</summary>
public interface IPaymentGatewayResolver
{
    Task<IPaymentGateway?> ResolveAsync(CancellationToken ct = default);

    /// <summary>Pelo nome que veio na URL do webhook.</summary>
    Task<IPaymentGateway?> ResolveAsync(string name, CancellationToken ct = default);
}
