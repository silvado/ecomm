using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ecommerce.Application.Billing;
using Ecommerce.Application.Payments;

namespace Ecommerce.Infrastructure.Billing;

public sealed class BillingOptions
{
    public const string Section = "Billing";

    /// <summary>Gateway simulado com página de pagamento (só dev e testes; a API recusa subir com ele em produção).</summary>
    public bool UseFakeGateway { get; set; }

    /// <summary>Antecedência com que a mensalidade é gerada antes do vencimento (HIPÓTESE: 10 dias).</summary>
    public int InvoiceLeadDays { get; set; } = 10;
}

/// <summary>
/// Gateway da plataforma de mentira (regra 6 do CLAUDE.md). Sem estado, como o <see cref="Payments.FakePaymentGateway"/>:
/// o id da cobrança carrega fatura, valor e situação, então API e Worker respondem igual à consulta. Assina os avisos com
/// uma chave fixa de teste para o caminho do webhook ser o mesmo da produção.
/// </summary>
public sealed class FakeSubscriptionBilling : ISubscriptionBilling
{
    public const string GatewayName = "simulado";
    public const string SignatureHeader = "x-fake-signature";
    public const string SimulationPath = "/api/dev/assinatura-simulada";
    private const string Prefix = "fakesub";
    private static readonly byte[] TestKey = Encoding.ASCII.GetBytes("chave-da-cobranca-simulada");

    public string Name => GatewayName;

    public static string Sign(string chargeId, string requestId) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(TestKey, Encoding.UTF8.GetBytes($"{chargeId};{requestId}")));

    /// <summary>Cobrança paga: <c>fakesub.{fatura}.{centavos}.Paid.{único}</c> (a criada não tem situação: pendente).</summary>
    public static string PaidChargeId(string chargeId) => $"{chargeId}.{ChargeStatus.Paid}.{Guid.NewGuid():N}";

    public Task<SubscriptionCharge> CreateChargeAsync(SubscriptionChargeRequest request, CancellationToken ct = default)
    {
        var id = string.Create(CultureInfo.InvariantCulture, $"{Prefix}.{request.InvoiceId:N}.{(long)(request.Amount * 100)}");
        return Task.FromResult(new SubscriptionCharge(id, $"{SimulationPath}/{id}?volta={Uri.EscapeDataString(request.ReturnUrl)}"));
    }

    public Task CancelChargeAsync(string externalId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<BillingNotification> ReadNotificationAsync(WebhookRequest request, CancellationToken ct = default)
    {
        if (!request.Query.TryGetValue("id", out var chargeId) || !request.Headers.TryGetValue("x-request-id", out var requestId)
            || !request.Headers.TryGetValue(SignatureHeader, out var signature)
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(signature), Encoding.ASCII.GetBytes(Sign(chargeId, requestId))))
            return Task.FromResult(BillingNotification.Invalid);
        return Task.FromResult(request.Query.GetValueOrDefault("evento") == "cobranca"
            ? new BillingNotification(NotificationValidity.Payment, requestId, chargeId)
            : BillingNotification.Other);
    }

    public Task<GatewayCharge?> GetChargeAsync(string externalId, CancellationToken ct = default)
    {
        var parts = externalId.Split('.');
        if (parts is not [Prefix, var invoice, var cents, ..] || parts.Length is not (3 or 5)
            || !Guid.TryParseExact(invoice, "N", out var invoiceId)
            || !long.TryParse(cents, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            return Task.FromResult<GatewayCharge?>(null);
        var status = parts.Length == 5 && Enum.TryParse<ChargeStatus>(parts[3], out var parsed) ? parsed : ChargeStatus.Pending;
        return Task.FromResult<GatewayCharge?>(new GatewayCharge(externalId, status, status == ChargeStatus.Paid ? value / 100m : 0m, invoiceId.ToString()));
    }
}
