using System.Diagnostics;
using Ecommerce.Application.Integrations;
using Ecommerce.Application.Payments;
using Ecommerce.Application.Shipping;

namespace Ecommerce.Infrastructure.Integrations;

/// <summary>
/// Gateway de pagamento com cada chamada registrada no log de integração (RF29): operação em linguagem de negócio,
/// duração, sucesso ou erro resumido e o pedido, quando já se sabe. O comportamento do gateway não muda.
/// </summary>
public sealed class LoggedPaymentGateway(IPaymentGateway inner, IIntegrationLog log) : IPaymentGateway
{
    public string Name => inner.Name;

    public Task<PaymentCheckout> CreateCheckoutAsync(PaymentCheckoutRequest request, CancellationToken ct = default) =>
        Logged.RunAsync(log, Name, "criar cobrança", () => inner.CreateCheckoutAsync(request, ct), _ => null, request.OrderId, null, ct);

    public Task<PaymentNotification> ReadNotificationAsync(WebhookRequest request, CancellationToken ct = default) =>
        Logged.RunAsync(log, Name, "validar aviso de pagamento", () => inner.ReadNotificationAsync(request, ct), n => n.PaymentId, null, null, ct,
            failed: n => n.Validity == NotificationValidity.Invalid ? "Aviso recusado: assinatura ausente ou inválida." : null);

    public Task<GatewayPayment?> GetPaymentAsync(string paymentId, CancellationToken ct = default) =>
        Logged.RunAsync(log, Name, "consultar pagamento", () => inner.GetPaymentAsync(paymentId, ct), _ => paymentId, null, null, ct,
            failed: p => p is null ? "Pagamento não encontrado nesta conta do gateway." : null);
}

/// <summary>Provedor de frete com cada cotação registrada no log de integração (RF29).</summary>
public sealed class LoggedShippingProvider(IShippingProvider inner, string channel, IIntegrationLog log) : IShippingProvider
{
    public Task<IReadOnlyList<ShippingOption>> QuoteAsync(ShippingQuoteRequest request, CancellationToken ct = default) =>
        Logged.RunAsync(log, channel, "cotar frete", () => inner.QuoteAsync(request, ct), _ => $"CEP {request.To.Value}", null, null, ct);
}

internal static class Logged
{
    /// <summary>
    /// Executa e registra. Exceção: registrada como falha e relançada. <paramref name="failed"/>: resultado que, sem
    /// exceção, ainda é falha de negócio (ex.: aviso com assinatura inválida).
    /// </summary>
    public static async Task<T> RunAsync<T>(IIntegrationLog log, string channel, string operation, Func<Task<T>> call, Func<T, string?> reference,
        Guid? orderId, Guid? partId, CancellationToken ct, Func<T, string?>? failed = null)
    {
        var watch = Stopwatch.StartNew();
        T result;
        try
        {
            result = await call();
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await log.RecordAsync(channel, operation, false, watch.Elapsed, e.Message, null, orderId, partId, ct);
            throw;
        }
        var problem = failed?.Invoke(result);
        await log.RecordAsync(channel, operation, problem is null, watch.Elapsed, problem, reference(result), orderId, partId, ct);
        return result;
    }
}
