using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ecommerce.Application.Payments;
using Ecommerce.Domain.Payments;

namespace Ecommerce.Infrastructure.Payments;

public sealed class PaymentOptions
{
    public const string Section = "Payments";

    /// <summary>Gateway fake com página de pagamento simulado (só dev e testes; a API recusa subir com ele em produção).</summary>
    public bool UseFakeGateway { get; set; }

    public MercadoPagoOptions MercadoPago { get; set; } = new();

    public CieloOptions Cielo { get; set; } = new();
}

/// <summary>
/// Gateway de mentira para dev e testes (regra 6 do CLAUDE.md). Sem estado: o id do pagamento simulado carrega pedido,
/// status e valor, então API e worker respondem igual à consulta. Assina as notificações como um gateway real (HMAC
/// com uma chave fixa de teste) para o caminho do webhook ser o mesmo da produção.
/// </summary>
public sealed class FakePaymentGateway(Guid tenantId) : IPaymentGateway
{
    public const string GatewayName = "fake";
    public const string SignatureHeader = "x-fake-signature";
    public const string SimulationPath = "/api/dev/pagamento-simulado";
    private static readonly byte[] TestKey = Encoding.ASCII.GetBytes("chave-do-gateway-fake");

    public string Name => GatewayName;

    public static string Sign(string paymentId, string requestId) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(TestKey, Encoding.UTF8.GetBytes($"{paymentId};{requestId}")));

    /// <summary>Id do pagamento simulado: <c>fake.{pedido}.{status}.{centavos}.{único}</c>.</summary>
    public static string PaymentId(Guid orderId, PaymentStatus status, decimal amount) =>
        string.Create(CultureInfo.InvariantCulture, $"fake.{orderId:N}.{status}.{(long)(amount * 100)}.{Guid.NewGuid():N}");

    public Task<PaymentCheckout> CreateCheckoutAsync(PaymentCheckoutRequest request, CancellationToken ct = default)
    {
        var query = string.Create(CultureInfo.InvariantCulture,
            $"numero={request.OrderNumber}&total={(long)(request.Total * 100)}&volta={Uri.EscapeDataString(request.ReturnUrl)}");
        return Task.FromResult(new PaymentCheckout($"{SimulationPath}/{tenantId}/{request.OrderId}?{query}"));
    }

    public Task<PaymentNotification> ReadNotificationAsync(WebhookRequest request, CancellationToken ct = default)
    {
        if (!request.Query.TryGetValue("data.id", out var paymentId) || !request.Headers.TryGetValue("x-request-id", out var requestId)
            || !request.Headers.TryGetValue(SignatureHeader, out var signature)
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(signature), Encoding.ASCII.GetBytes(Sign(paymentId, requestId))))
            return Task.FromResult(PaymentNotification.Invalid);
        return Task.FromResult(request.Query.GetValueOrDefault("type") == "payment"
            ? new PaymentNotification(NotificationValidity.Payment, requestId, paymentId)
            : PaymentNotification.Other);
    }

    public Task<GatewayPayment?> GetPaymentAsync(string paymentId, CancellationToken ct = default)
    {
        var parts = paymentId.Split('.');
        return Task.FromResult(parts is ["fake", var order, var status, var cents, _]
            && Guid.TryParseExact(order, "N", out var orderId) && Enum.TryParse<PaymentStatus>(status, out var parsed)
            && long.TryParse(cents, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? new GatewayPayment(paymentId, parsed, value / 100m, orderId.ToString(), "simulado")
                : null);
    }
}
