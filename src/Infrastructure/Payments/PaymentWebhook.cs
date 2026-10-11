using Ecommerce.Application.Payments;
using Ecommerce.Application.Tenancy;
using Ecommerce.Domain.Payments;
using Ecommerce.Infrastructure.Tenancy;
using Wolverine;

namespace Ecommerce.Infrastructure.Payments;

/// <summary>
/// Webhook de pagamento (RF15 CA3, RF26, regra 7 do CLAUDE.md): confere a assinatura com as credenciais da loja da URL,
/// grava a notificação com chave de idempotência e responde. A consulta ao gateway e a baixa ficam para a fila.
/// </summary>
public sealed class PaymentWebhook(ITenantCatalog catalog, TenantScope scope, IPaymentGatewayResolver gateways, IMessageBus bus) : IPaymentWebhook
{
    public async Task<WebhookOutcome> ReceiveAsync(Guid tenantId, string gateway, WebhookRequest request, CancellationToken ct = default)
    {
        // Loja suspensa continua recebendo: um pagamento já feito precisa ser registrado.
        if (await catalog.FindByIdAsync(tenantId, ct) is null) return WebhookOutcome.NotFound;
        scope.Set(tenantId);
        if (await gateways.ResolveAsync(gateway, ct) is not { } resolved) return WebhookOutcome.NotFound;

        var notification = await resolved.ReadNotificationAsync(request, ct);
        if (notification.Validity == NotificationValidity.Other) return WebhookOutcome.Ignored;
        if (notification is not { Validity: NotificationValidity.Payment, IdempotencyKey: { Length: > 0 and <= ExternalEvent.KeyMaxLength } key,
                PaymentId: { Length: > 0 and <= ExternalEvent.ResourceIdMaxLength } paymentId })
            return WebhookOutcome.Rejected;

        var outcome = await bus.InvokeForTenantAsync<RecordOutcome>(tenantId.ToString(), new RecordPaymentNotification(resolved.Name, key, paymentId), ct);
        return outcome == RecordOutcome.Recorded ? WebhookOutcome.Accepted : WebhookOutcome.Ignored;
    }
}
