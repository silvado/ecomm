using System.Security.Cryptography;
using System.Text;
using Ecommerce.Application.Channels;
using Ecommerce.Application.Integrations;
using Ecommerce.Application.Vault;
using Ecommerce.Domain.Channels;
using Ecommerce.Domain.Platform;
using Ecommerce.Domain.Vault;
using Ecommerce.Infrastructure.Integrations;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Channels.MercadoLivre;

/// <summary>
/// Canais de venda da loja do escopo (RF21 CA1): conectar o Mercado Livre por OAuth (authorization code + PKCE S256), ver a
/// situação e desconectar. O <c>state</c> liga a volta do ML à loja e a quem pediu; vale 10 minutos e uma vez só.
/// Uma loja tem no máximo uma conta do ML ligada (para trocar, desconecta antes).
/// </summary>
public sealed class ChannelConnectionService(
    TenantDbContext db, ISecretVault vault, IMercadoLivreAuth auth, ChannelService channels, PlanDirectory plans, IIntegrationLog log,
    TimeProvider clock) : IChannelConnections
{
    public async Task<ChannelsOverview> OverviewAsync(CancellationToken ct = default)
    {
        var plan = await plans.ForTenantAsync(db.RequireTenantId(), ct);
        var connections = await db.ChannelConnections.AsNoTracking()
            .Where(c => c.Channel != FakeChannelProcessor.ChannelName)
            .OrderBy(c => c.Channel).ThenBy(c => c.CreatedAt)
            .ToListAsync(ct);
        return new ChannelsOverview(auth.Configured, plan.Has(PlanFeature.ForChannel(SalesChannel.MercadoLivre)), connections.Select(View).ToList());
    }

    public async Task<ConnectResult> StartMercadoLivreAsync(Guid userId, CancellationToken ct = default)
    {
        if (await NotAllowedAsync(ct) is { } refused) return refused;

        var tenantId = db.RequireTenantId();
        var now = clock.GetUtcNow();
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64)); // 86 caracteres (PKCE: 43 a 128)
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        // Pendências velhas da loja saem aqui (nada mais as usa depois de vencidas).
        await db.ChannelAuthorizations.Where(a => a.ExpiresAt < now).ExecuteDeleteAsync(ct);
        db.ChannelAuthorizations.Add(new ChannelAuthorization(tenantId, SalesChannel.MercadoLivre, Hash(state), verifier, userId, now));
        await db.SaveChangesAsync(ct);
        return new ConnectResult(ConnectOutcome.Done, AuthorizationUrl: auth.AuthorizationUrl(state, challenge));
    }

    public async Task<ConnectResult> CompleteMercadoLivreAsync(string? code, string? state, string? denial, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(state) || state.Length > 100) return Invalid("A volta do Mercado Livre veio sem identificação. Clique em Conectar de novo.");
        var hash = Hash(state);
        var pending = await db.ChannelAuthorizations.SingleOrDefaultAsync(a => a.StateHash == hash, ct);
        if (pending is null || pending.IsExpired(clock.GetUtcNow()))
            return Invalid("Esta autorização venceu ou já foi usada. Clique em Conectar de novo.");
        // Uso único: sai antes de qualquer coisa.
        db.ChannelAuthorizations.Remove(pending);
        await db.SaveChangesAsync(ct);

        if (!string.IsNullOrEmpty(denial)) return Invalid("A autorização foi negada no Mercado Livre. Nada foi conectado.");
        if (string.IsNullOrWhiteSpace(code) || code.Length > 200) return Invalid("A volta do Mercado Livre veio sem o código de autorização. Clique em Conectar de novo.");
        if (await NotAllowedAsync(ct) is { } refused) return refused;

        OAuthTokens tokens;
        try
        {
            tokens = await Logged.RunAsync(log, SalesChannel.MercadoLivre, "autorizar conta", () => auth.ExchangeCodeAsync(code, pending.CodeVerifier, ct),
                t => $"vendedor {t.UserId}", null, null, ct);
        }
        catch (ChannelAuthorizationRevokedException e)
        {
            return Invalid(e.Message);
        }
        catch (ChannelConnectorException e)
        {
            return new ConnectResult(ConnectOutcome.Unavailable, e.Message);
        }

        var other = await db.ChannelConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Channel == SalesChannel.MercadoLivre
            && c.ExternalAccountId != tokens.UserId && c.Status != ChannelConnectionStatus.Disabled, ct);
        if (other is not null)
            return new ConnectResult(ConnectOutcome.NotAllowed,
                $"A loja já está ligada à conta {other.AccountName ?? other.ExternalAccountId} do Mercado Livre. Desconecte-a antes de ligar outra.");

        string? nickname;
        try { nickname = await auth.AccountNameAsync(tokens.AccessToken, ct); }
        catch (ChannelConnectorException) { nickname = null; } // só o apelido: a conexão vale sem ele

        ChannelConnection connection;
        try
        {
            connection = await channels.ConnectAsync(SalesChannel.MercadoLivre, tokens.UserId, ct);
        }
        catch (InvalidOperationException e) when (e.Message == ChannelService.AccountInUseMessage)
        {
            return new ConnectResult(ConnectOutcome.AccountInUse, "Esta conta do Mercado Livre já está ligada a outra loja da plataforma.");
        }
        await MercadoLivreCredentials.SaveAsync(vault, tokens, ct);
        connection.Authorized(nickname, tokens.ExpiresAt, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return new ConnectResult(ConnectOutcome.Done, Connection: View(connection));
    }

    public async Task<bool> DisconnectAsync(Guid connectionId, CancellationToken ct = default)
    {
        var connection = await db.ChannelConnections.SingleOrDefaultAsync(c => c.Id == connectionId && c.Channel == SalesChannel.MercadoLivre, ct);
        if (connection is null || connection.Status == ChannelConnectionStatus.Disabled) return false;

        if (await MercadoLivreCredentials.ReadAsync(vault, connection.ExternalAccountId, ct) is { } tokens)
        {
            try
            {
                await Logged.RunAsync(log, SalesChannel.MercadoLivre, "revogar acesso", async () => { await auth.RevokeAsync(tokens.AccessToken, tokens.UserId, ct); return true; },
                    _ => connection.AccountName ?? connection.ExternalAccountId, null, null, ct);
            }
            catch (Exception e) when (e is ChannelConnectorException or ChannelAuthorizationRevokedException)
            {
                // Melhor esforço: o acesso some daqui de qualquer jeito (e o ML o derruba sozinho em 6 h). A falha fica no log.
            }
        }
        await vault.DeleteAsync(SecretKind.OAuthToken, MercadoLivreCredentials.SecretName(connection.ExternalAccountId), ct);
        connection.Disconnect(clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await channels.UnmapAccountAsync(connection.Channel, connection.ExternalAccountId, ct);
        return true;
    }

    private async Task<ConnectResult?> NotAllowedAsync(CancellationToken ct)
    {
        if (!auth.Configured) return new ConnectResult(ConnectOutcome.NotAllowed, "O Mercado Livre ainda não está disponível nesta instalação.");
        var plan = await plans.ForTenantAsync(db.RequireTenantId(), ct);
        return plan.Has(PlanFeature.ForChannel(SalesChannel.MercadoLivre))
            ? null
            : new ConnectResult(ConnectOutcome.NotAllowed, PlanDirectory.FeatureMessage(plan, "o canal Mercado Livre"));
    }

    private static ConnectResult Invalid(string message) => new(ConnectOutcome.Invalid, message);

    private static ChannelConnectionView View(ChannelConnection c) =>
        new(c.Id, c.Channel, c.ExternalAccountId, c.AccountName, c.Status, c.StatusReason, c.CreatedAt, c.UpdatedAt);

    private static string Hash(string state) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
