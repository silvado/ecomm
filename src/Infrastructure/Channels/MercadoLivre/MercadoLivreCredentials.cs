using System.Text.Json;
using Ecommerce.Application.Channels;
using Ecommerce.Application.Integrations;
using Ecommerce.Application.Tenancy;
using Ecommerce.Application.Vault;
using Ecommerce.Domain.Channels;
using Ecommerce.Domain.Vault;
using Ecommerce.Infrastructure.Integrations;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ecommerce.Infrastructure.Channels.MercadoLivre;

public enum RenewOutcome
{
    /// <summary>Ainda longe de vencer: nada feito.</summary>
    NotDue,
    Renewed,
    /// <summary>O ML recusou: a conexão ficou "reautorizar".</summary>
    ReauthRequired,
    /// <summary>Conexão não está ativa (desconectada ou já pedindo nova autorização).</summary>
    Inactive,
}

/// <summary>
/// Tokens do Mercado Livre da loja do escopo, no cofre (RF06): um segredo por conta com o par access/refresh e o vencimento.
/// O refresh token é de uso único: a renovação trava a linha da conexão (<c>FOR UPDATE</c>) e relê o cofre antes de chamar o
/// ML, para a API e o Worker nunca gastarem o mesmo refresh duas vezes. Roda num escopo e numa transação próprios: não
/// depende da transação de quem chamou, e a marca "reautorizar" fica gravada mesmo que a operação de quem chamou falhe.
/// </summary>
public sealed class MercadoLivreCredentials(IServiceScopeFactory scopes, ITenantContext tenant, IMercadoLivreAuth auth, IOptions<MercadoLivreOptions> options,
    TimeProvider clock)
{
    /// <summary>Margem do acesso para uso imediato: abaixo disto, renova antes de chamar a API.</summary>
    public static readonly TimeSpan UseMargin = TimeSpan.FromMinutes(5);

    private sealed record StoredTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

    public static string SecretName(string userId) => $"{SalesChannel.MercadoLivre}:{userId}";

    /// <summary>Grava o par de tokens (na conexão nova ou ao autorizar de novo).</summary>
    public static Task SaveAsync(ISecretVault vault, OAuthTokens tokens, CancellationToken ct) =>
        vault.SetAsync(SecretKind.OAuthToken, SecretName(tokens.UserId),
            JsonSerializer.SerializeToUtf8Bytes(new StoredTokens(tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresAt)), ct: ct);

    public static async Task<OAuthTokens?> ReadAsync(ISecretVault vault, string userId, CancellationToken ct)
    {
        if (await vault.GetAsync(SecretKind.OAuthToken, SecretName(userId), ct) is not { Length: > 0 } bytes) return null;
        var stored = JsonSerializer.Deserialize<StoredTokens>(bytes)!;
        return new OAuthTokens(stored.AccessToken, stored.RefreshToken, stored.ExpiresAt, userId);
    }

    /// <summary>Acesso válido para chamar a API agora (renova antes, se preciso).</summary>
    /// <exception cref="ChannelAuthorizationRevokedException">A conexão pede nova autorização.</exception>
    /// <exception cref="ChannelConnectorException">ML fora do ar na renovação: a fila tenta de novo.</exception>
    public async Task<string> AccessTokenAsync(Guid connectionId, CancellationToken ct = default)
    {
        var (outcome, token, reason) = await EnsureAsync(connectionId, UseMargin, ct);
        return outcome is RenewOutcome.NotDue or RenewOutcome.Renewed
            ? token!
            : throw new ChannelAuthorizationRevokedException(reason ?? "Conexão com o Mercado Livre inativa. Conecte a conta de novo.");
    }

    /// <summary>RF21 CA2: renova quem vence dentro da janela configurada (o Worker chama de hora em hora).</summary>
    public async Task<RenewOutcome> RenewIfDueAsync(Guid connectionId, CancellationToken ct = default) =>
        (await EnsureAsync(connectionId, options.Value.RenewBefore, ct)).Outcome;

    private async Task<(RenewOutcome Outcome, string? AccessToken, string? Reason)> EnsureAsync(Guid connectionId, TimeSpan window, CancellationToken ct)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Credenciais do canal sem loja no escopo.");
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
        var vault = scope.ServiceProvider.GetRequiredService<ISecretVault>();
        var log = scope.ServiceProvider.GetRequiredService<IIntegrationLog>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var locked = await db.Database.SqlQuery<Guid>($"""SELECT id AS "Value" FROM channel_connections WHERE id = {connectionId} FOR UPDATE""").ToListAsync(ct);
        if (locked.Count == 0) throw new InvalidOperationException("Conexão não encontrada.");
        var connection = await db.ChannelConnections.SingleAsync(c => c.Id == connectionId, ct);
        if (connection.Status != ChannelConnectionStatus.Active) return (RenewOutcome.Inactive, null, connection.StatusReason);

        var now = clock.GetUtcNow();
        var tokens = await ReadAsync(vault, connection.ExternalAccountId, ct);
        if (tokens is null)
        {
            connection.RequireReauthorization("O acesso ao Mercado Livre não foi encontrado. Conecte a conta de novo.", now);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return (RenewOutcome.ReauthRequired, null, connection.StatusReason);
        }
        if (tokens.ExpiresAt - now > window) return (RenewOutcome.NotDue, tokens.AccessToken, null);

        OAuthTokens renewed;
        try
        {
            renewed = await Logged.RunAsync(log, SalesChannel.MercadoLivre, "renovar acesso", () => auth.RefreshAsync(tokens.RefreshToken, ct),
                _ => connection.AccountName ?? connection.ExternalAccountId, null, null, ct);
        }
        catch (ChannelAuthorizationRevokedException e)
        {
            // RF21 CA2: marca para reautorizar; o alerta é a conexão na tela e a faixa do painel (resumo de integrações).
            connection.RequireReauthorization(e.Message, clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return (RenewOutcome.ReauthRequired, null, connection.StatusReason);
        }

        // O refresh antigo já não vale: grava o novo antes de qualquer outra coisa.
        await SaveAsync(vault, renewed, ct);
        connection.TokenRenewed(renewed.ExpiresAt, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (RenewOutcome.Renewed, renewed.AccessToken, null);
    }
}
