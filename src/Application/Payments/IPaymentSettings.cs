namespace Ecommerce.Application.Payments;

/// <summary>Uma credencial como o painel a vê (RF06 CA3): nunca o valor, só os 4 últimos caracteres.</summary>
public sealed record CredentialHint(string Field, string Hint);

/// <summary>
/// Conta de um gateway da loja. Com mais de um conectado, cobra o salvo por último (<paramref name="Active"/>); os
/// webhooks de todos continuam valendo para pagamentos já iniciados. <paramref name="NotificationUrl"/> é o webhook
/// desta loja para aquele gateway, para o Dono colar no portal dele.
/// </summary>
public sealed record GatewaySettings(
    string Gateway, bool Configured, bool Active, IReadOnlyList<CredentialHint> Credentials, DateTimeOffset? UpdatedAt, string NotificationUrl);

public enum SaveGatewayOutcome
{
    Saved,
    UnknownGateway,
    Invalid,
}

public sealed record SaveGatewayResult(SaveGatewayOutcome Outcome, IReadOnlyList<GatewaySettings>? Settings, string? Error);

/// <summary>Credenciais de pagamento da loja do escopo (RF15), coladas pelo Dono e guardadas no cofre.</summary>
public interface IPaymentSettings
{
    Task<IReadOnlyList<GatewaySettings>> ListAsync(CancellationToken ct = default);

    /// <summary>Grava todas as credenciais do gateway juntas (campos: ver cada gateway); qualquer uma inválida, nada é gravado.</summary>
    Task<SaveGatewayResult> SaveAsync(string gateway, IReadOnlyDictionary<string, string?> credentials, CancellationToken ct = default);

    /// <summary>Nulo se o gateway não existe.</summary>
    Task<IReadOnlyList<GatewaySettings>?> RemoveAsync(string gateway, CancellationToken ct = default);
}
