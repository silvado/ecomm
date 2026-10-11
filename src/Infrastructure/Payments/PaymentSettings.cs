using System.Text;
using Ecommerce.Application.Payments;
using Ecommerce.Application.Tenancy;
using Ecommerce.Application.Vault;
using Ecommerce.Domain.Vault;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ecommerce.Infrastructure.Payments;

/// <summary>
/// Credenciais do Mercado Pago da loja (RF15, HIPÓTESE Q28): Access Token e chave secreta do webhook, coladas pelo Dono
/// e guardadas no cofre (RF06). Só o formato é conferido; credencial errada aparece ao abrir o primeiro pagamento.
/// </summary>
public sealed class PaymentSettings(
    ISecretVault vault, TenantDbContext db, ITenantContext tenant, ITenantCatalog catalog, IOptions<PlatformOptions> platform) : IPaymentSettings
{
    private const int MinLength = 16;
    private const int MaxLength = 500;

    public async Task<MercadoPagoSettings> GetMercadoPagoAsync(CancellationToken ct = default)
    {
        var secrets = (await vault.ListAsync(ct)).Where(s => s.Kind == SecretKind.PaymentGatewayCredential).ToList();
        var token = secrets.SingleOrDefault(s => s.Name == PaymentGatewayResolver.MercadoPagoAccessTokenName);
        var secret = secrets.SingleOrDefault(s => s.Name == PaymentGatewayResolver.MercadoPagoWebhookSecretName);
        return new MercadoPagoSettings(token is not null && secret is not null, token?.Hint, secret?.Hint,
            new[] { token?.UpdatedAt, secret?.UpdatedAt }.Max(), await NotificationUrlAsync(ct));
    }

    public async Task<SaveMercadoPagoResult> SaveMercadoPagoAsync(SaveMercadoPagoCredentials credentials, CancellationToken ct = default)
    {
        var token = credentials.AccessToken?.Trim();
        var secret = credentials.WebhookSecret?.Trim();
        if (!IsValid(token)) return new(null, "Informe o Access Token de produção da sua aplicação no Mercado Pago.");
        if (!IsValid(secret)) return new(null, "Informe a chave secreta das notificações (Webhooks) da sua aplicação no Mercado Pago.");

        // As duas juntas ou nenhuma: o gateway só é usado com ambas no cofre.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await vault.SetAsync(SecretKind.PaymentGatewayCredential, PaymentGatewayResolver.MercadoPagoAccessTokenName, Encoding.UTF8.GetBytes(token!), ct: ct);
        await vault.SetAsync(SecretKind.PaymentGatewayCredential, PaymentGatewayResolver.MercadoPagoWebhookSecretName, Encoding.UTF8.GetBytes(secret!), ct: ct);
        await transaction.CommitAsync(ct);
        return new(await GetMercadoPagoAsync(ct), null);
    }

    public async Task<MercadoPagoSettings> RemoveMercadoPagoAsync(CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await vault.DeleteAsync(SecretKind.PaymentGatewayCredential, PaymentGatewayResolver.MercadoPagoAccessTokenName, ct);
        await vault.DeleteAsync(SecretKind.PaymentGatewayCredential, PaymentGatewayResolver.MercadoPagoWebhookSecretName, ct);
        await transaction.CommitAsync(ct);
        return await GetMercadoPagoAsync(ct);
    }

    private static bool IsValid(string? value) =>
        value is { Length: >= MinLength and <= MaxLength } && value.All(c => c is > ' ' and <= '~');

    /// <summary>No subdomínio da plataforma, que sempre aponta para a loja (domínio próprio pode mudar).</summary>
    private async Task<string> NotificationUrlAsync(CancellationToken ct)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Configuração de pagamento sem loja no escopo.");
        var path = $"/api/webhooks/pagamentos/{MercadoPagoGateway.GatewayName}/{tenantId}";
        return platform.Value.Domain is { Length: > 0 } domain && await catalog.FindByIdAsync(tenantId, ct) is { } store
            ? $"https://{store.Slug}.{domain}{path}"
            : path;
    }
}
