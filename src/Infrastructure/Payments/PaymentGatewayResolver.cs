using System.Text;
using Ecommerce.Application.Payments;
using Ecommerce.Application.Tenancy;
using Ecommerce.Application.Vault;
using Ecommerce.Domain.Vault;
using Microsoft.Extensions.Options;

namespace Ecommerce.Infrastructure.Payments;

/// <summary>
/// Gateway da loja do escopo: o Mercado Pago, se o Dono cadastrou as credenciais no cofre (RF06); senão o fake em
/// dev/testes; senão nenhum (a loja não recebe pagamento online).
/// </summary>
public sealed class PaymentGatewayResolver(
    ISecretVault vault, IHttpClientFactory httpClients, IOptions<PaymentOptions> options, ITenantContext tenant, TimeProvider clock) : IPaymentGatewayResolver
{
    public const string HttpClientName = "mercado-pago";
    public const string MercadoPagoAccessTokenName = "mercado-pago:access-token";
    public const string MercadoPagoWebhookSecretName = "mercado-pago:webhook-secret";

    public async Task<IPaymentGateway?> ResolveAsync(CancellationToken ct = default) =>
        await MercadoPagoAsync(ct) ?? (IPaymentGateway?)Fake();

    public async Task<IPaymentGateway?> ResolveAsync(string name, CancellationToken ct = default) => name switch
    {
        MercadoPagoGateway.GatewayName => await MercadoPagoAsync(ct),
        FakePaymentGateway.GatewayName => Fake(),
        _ => null,
    };

    private async Task<MercadoPagoGateway?> MercadoPagoAsync(CancellationToken ct)
    {
        var token = await vault.GetAsync(SecretKind.PaymentGatewayCredential, MercadoPagoAccessTokenName, ct);
        var secret = await vault.GetAsync(SecretKind.PaymentGatewayCredential, MercadoPagoWebhookSecretName, ct);
        return token is { Length: > 0 } && secret is { Length: > 0 }
            ? new MercadoPagoGateway(httpClients.CreateClient(HttpClientName), options.Value.MercadoPago,
                Encoding.UTF8.GetString(token), Encoding.UTF8.GetString(secret), clock)
            : null;
    }

    private FakePaymentGateway? Fake() =>
        options.Value.UseFakeGateway && tenant.TenantId is { } tenantId ? new FakePaymentGateway(tenantId) : null;
}
