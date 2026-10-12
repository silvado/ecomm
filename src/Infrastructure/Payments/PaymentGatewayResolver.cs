using System.Text;
using Ecommerce.Application.Integrations;
using Ecommerce.Application.Payments;
using Ecommerce.Application.Tenancy;
using Ecommerce.Application.Vault;
using Ecommerce.Domain.Vault;
using Ecommerce.Infrastructure.Integrations;
using Microsoft.Extensions.Options;

namespace Ecommerce.Infrastructure.Payments;

/// <summary>Uma credencial de um gateway: nome no formulário do painel e nome do segredo no cofre.</summary>
public sealed record CredentialField(string Field, string SecretName, string Label, Func<string, bool>? Format = null);

/// <summary>Gateways que o Dono pode conectar e as credenciais de cada um (todas no cofre, tipo PaymentGatewayCredential).</summary>
public static class PaymentAccounts
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<CredentialField>> Fields = new Dictionary<string, IReadOnlyList<CredentialField>>
    {
        [MercadoPagoGateway.GatewayName] =
        [
            new("accessToken", PaymentGatewayResolver.MercadoPagoAccessTokenName, "Access Token de produção"),
            new("webhookSecret", PaymentGatewayResolver.MercadoPagoWebhookSecretName, "assinatura secreta das notificações (Webhooks)"),
        ],
        [CieloGateway.GatewayName] =
        [
            new("merchantId", "cielo:merchant-id", "MerchantId (formato 00000000-0000-0000-0000-000000000000)", v => Guid.TryParse(v, out _)),
            new("clientId", "cielo:client-id", "Client ID das credenciais de API"),
            new("clientSecret", "cielo:client-secret", "Client Secret das credenciais de API"),
        ],
    };
}

/// <summary>
/// Gateway da loja do escopo: entre os conectados no cofre (RF06), o salvo por último; senão o fake em dev/testes;
/// senão nenhum (a loja não recebe pagamento online). Pelo nome (webhook), qualquer um conectado responde.
/// </summary>
public sealed class PaymentGatewayResolver(
    ISecretVault vault, IHttpClientFactory httpClients, IOptions<PaymentOptions> options, ITenantContext tenant, TimeProvider clock,
    IIntegrationLog integrationLog) : IPaymentGatewayResolver
{
    public const string HttpClientName = "pagamentos";
    public const string MercadoPagoAccessTokenName = "mercado-pago:access-token";
    public const string MercadoPagoWebhookSecretName = "mercado-pago:webhook-secret";

    // Toda chamada ao gateway passa pelo log de integração (RF29).
    public async Task<IPaymentGateway?> ResolveAsync(CancellationToken ct = default) =>
        Logged(await ActiveAsync(ct) is { } name ? await ConnectedAsync(name, ct) : Fake());

    public async Task<IPaymentGateway?> ResolveAsync(string name, CancellationToken ct = default) =>
        Logged(name == FakePaymentGateway.GatewayName ? Fake() : await ConnectedAsync(name, ct));

    private LoggedPaymentGateway? Logged(IPaymentGateway? gateway) => gateway is null ? null : new LoggedPaymentGateway(gateway, integrationLog);

    /// <summary>O gateway conectado com credenciais salvas por último (só metadados; nada é decifrado).</summary>
    public async Task<string?> ActiveAsync(CancellationToken ct = default)
    {
        var saved = (await vault.ListAsync(ct)).Where(s => s.Kind == SecretKind.PaymentGatewayCredential).ToDictionary(s => s.Name);
        return PaymentAccounts.Fields
            .Where(g => g.Value.All(f => saved.ContainsKey(f.SecretName)))
            .Select(g => (Name: g.Key, UpdatedAt: g.Value.Max(f => saved[f.SecretName].UpdatedAt)))
            .OrderByDescending(g => g.UpdatedAt)
            .Select(g => g.Name)
            .FirstOrDefault();
    }

    private async Task<IPaymentGateway?> ConnectedAsync(string name, CancellationToken ct)
    {
        if (!PaymentAccounts.Fields.TryGetValue(name, out var fields)) return null;
        var values = new Dictionary<string, string>();
        foreach (var field in fields)
        {
            if (await vault.GetAsync(SecretKind.PaymentGatewayCredential, field.SecretName, ct) is not { Length: > 0 } value) return null;
            values[field.Field] = Encoding.UTF8.GetString(value);
        }

        var http = httpClients.CreateClient(HttpClientName);
        return name switch
        {
            MercadoPagoGateway.GatewayName => new MercadoPagoGateway(http, options.Value.MercadoPago, values["accessToken"], values["webhookSecret"], clock),
            CieloGateway.GatewayName => new CieloGateway(http, options.Value.Cielo, values["merchantId"], values["clientId"], values["clientSecret"]),
            _ => null,
        };
    }

    private FakePaymentGateway? Fake() =>
        options.Value.UseFakeGateway && tenant.TenantId is { } tenantId ? new FakePaymentGateway(tenantId) : null;
}
