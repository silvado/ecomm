namespace Ecommerce.Application.Payments;

/// <summary>
/// Conta do Mercado Pago da loja como o painel a vê (RF06 CA3): nunca os valores, só os 4 últimos caracteres.
/// <paramref name="NotificationUrl"/> é o webhook desta loja, para o Dono colar no Mercado Pago ao gerar a chave secreta.
/// </summary>
public sealed record MercadoPagoSettings(bool Configured, string? AccessTokenHint, string? WebhookSecretHint, DateTimeOffset? UpdatedAt, string NotificationUrl);

public sealed record SaveMercadoPagoCredentials(string? AccessToken, string? WebhookSecret);

public sealed record SaveMercadoPagoResult(MercadoPagoSettings? Settings, string? Error);

/// <summary>Credenciais de pagamento da loja do escopo (RF15), coladas pelo Dono e guardadas no cofre.</summary>
public interface IPaymentSettings
{
    Task<MercadoPagoSettings> GetMercadoPagoAsync(CancellationToken ct = default);

    Task<SaveMercadoPagoResult> SaveMercadoPagoAsync(SaveMercadoPagoCredentials credentials, CancellationToken ct = default);

    Task<MercadoPagoSettings> RemoveMercadoPagoAsync(CancellationToken ct = default);
}
