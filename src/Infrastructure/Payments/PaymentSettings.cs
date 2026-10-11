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
/// Contas de pagamento da loja (RF15, HIPÓTESES Q28 e Q30): credenciais coladas pelo Dono e guardadas no cofre (RF06).
/// Só o formato é conferido; credencial errada aparece ao abrir o primeiro pagamento.
/// </summary>
public sealed class PaymentSettings(
    ISecretVault vault, PaymentGatewayResolver resolver, TenantDbContext db, ITenantContext tenant, ITenantCatalog catalog,
    IOptions<PlatformOptions> platform) : IPaymentSettings
{
    private const int MinLength = 16;
    private const int MaxLength = 500;

    public async Task<IReadOnlyList<GatewaySettings>> ListAsync(CancellationToken ct = default)
    {
        var saved = (await vault.ListAsync(ct)).Where(s => s.Kind == SecretKind.PaymentGatewayCredential).ToDictionary(s => s.Name);
        var active = await resolver.ActiveAsync(ct);
        var origin = await StoreOriginAsync(ct);
        return PaymentAccounts.Fields.Select(g =>
        {
            var present = g.Value.Where(f => saved.ContainsKey(f.SecretName)).ToList();
            return new GatewaySettings(g.Key, present.Count == g.Value.Count, g.Key == active,
                present.Select(f => new CredentialHint(f.Field, saved[f.SecretName].Hint)).ToList(),
                present.Count > 0 ? present.Max(f => saved[f.SecretName].UpdatedAt) : null,
                $"{origin}/api/webhooks/pagamentos/{g.Key}/{TenantId()}");
        }).ToList();
    }

    public async Task<SaveGatewayResult> SaveAsync(string gateway, IReadOnlyDictionary<string, string?> credentials, CancellationToken ct = default)
    {
        if (!PaymentAccounts.Fields.TryGetValue(gateway, out var fields)) return new(SaveGatewayOutcome.UnknownGateway, null, null);

        var values = new List<(CredentialField Field, string Value)>();
        foreach (var field in fields)
        {
            var value = credentials.GetValueOrDefault(field.Field)?.Trim();
            if (!IsValid(field, value)) return new(SaveGatewayOutcome.Invalid, null, $"Informe o {field.Label}.");
            values.Add((field, value!));
        }

        // Todas juntas ou nenhuma: o gateway só é usado com o conjunto completo no cofre.
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            foreach (var (field, value) in values)
                await vault.SetAsync(SecretKind.PaymentGatewayCredential, field.SecretName, Encoding.UTF8.GetBytes(value), ct: ct);
            await transaction.CommitAsync(ct);
        }
        return new(SaveGatewayOutcome.Saved, await ListAsync(ct), null);
    }

    public async Task<IReadOnlyList<GatewaySettings>?> RemoveAsync(string gateway, CancellationToken ct = default)
    {
        if (!PaymentAccounts.Fields.TryGetValue(gateway, out var fields)) return null;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            foreach (var field in fields)
                await vault.DeleteAsync(SecretKind.PaymentGatewayCredential, field.SecretName, ct);
            await transaction.CommitAsync(ct);
        }
        return await ListAsync(ct);
    }

    private static bool IsValid(CredentialField field, string? value) =>
        value is { Length: <= MaxLength } && value.All(c => c is > ' ' and <= '~')
        && (field.Format is { } format ? format(value) : value.Length >= MinLength);

    private Guid TenantId() => tenant.TenantId ?? throw new InvalidOperationException("Configuração de pagamento sem loja no escopo.");

    /// <summary>No subdomínio da plataforma, que sempre aponta para a loja (domínio próprio pode mudar).</summary>
    private async Task<string> StoreOriginAsync(CancellationToken ct) =>
        platform.Value.Domain is { Length: > 0 } domain && await catalog.FindByIdAsync(TenantId(), ct) is { } store
            ? $"https://{store.Slug}.{domain}"
            : string.Empty;
}
