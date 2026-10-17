using Ecommerce.Domain.Channels;

namespace Ecommerce.Application.Channels;

/// <summary>Acesso a uma conta do canal (OAuth). <see cref="UserId"/>: id do vendedor no canal.</summary>
public sealed record OAuthTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string UserId);

/// <summary>
/// O canal recusou a autorização ou a renovação (código inválido, acesso revogado, senha trocada, vencido): não adianta
/// tentar de novo — só a loja resolve, autorizando outra vez (RF21 CA2).
/// </summary>
public sealed class ChannelAuthorizationRevokedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// OAuth do Mercado Livre (RF21 CA1/CA2): um app da plataforma, autorizado por cada loja. Implementações: a API real e a
/// simulada (dev). Falha de comunicação: <see cref="ChannelConnectorException"/>; recusa: <see cref="ChannelAuthorizationRevokedException"/>.
/// </summary>
public interface IMercadoLivreAuth
{
    /// <summary>Falso quando a instalação não tem o app do ML configurado.</summary>
    bool Configured { get; }

    /// <summary>Endereço da página de autorização do ML (PKCE S256).</summary>
    string AuthorizationUrl(string state, string codeChallenge);

    Task<OAuthTokens> ExchangeCodeAsync(string code, string codeVerifier, CancellationToken ct = default);

    /// <summary>O refresh token é de uso único: a resposta traz o próximo, que substitui o anterior.</summary>
    Task<OAuthTokens> RefreshAsync(string refreshToken, CancellationToken ct = default);

    /// <summary>Apelido do vendedor (para a loja reconhecer a conta).</summary>
    Task<string?> AccountNameAsync(string accessToken, CancellationToken ct = default);

    /// <summary>Tira a autorização do app na conta do vendedor.</summary>
    Task RevokeAsync(string accessToken, string userId, CancellationToken ct = default);
}

/// <summary>Uma conexão da loja, como o painel mostra (nunca os tokens).</summary>
public sealed record ChannelConnectionView(
    Guid Id, string Channel, string ExternalAccountId, string? AccountName, ChannelConnectionStatus Status, string? StatusReason,
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

/// <param name="MercadoLivreAvailable">A instalação tem o app do ML configurado.</param>
/// <param name="MercadoLivreInPlan">O plano da loja inclui o Mercado Livre (RF37).</param>
public sealed record ChannelsOverview(bool MercadoLivreAvailable, bool MercadoLivreInPlan, IReadOnlyList<ChannelConnectionView> Connections);

public enum ConnectOutcome
{
    Done,
    /// <summary>Instalação sem o app, fora do plano ou loja já ligada a outra conta.</summary>
    NotAllowed,
    /// <summary>Volta sem autorização válida: state desconhecido ou vencido, recusa da loja ou código recusado.</summary>
    Invalid,
    /// <summary>Esta conta do canal já está ligada a outra loja.</summary>
    AccountInUse,
    /// <summary>O canal não respondeu: tentar de novo.</summary>
    Unavailable,
}

/// <param name="AuthorizationUrl">Ao começar: para onde o navegador vai (página de autorização do canal).</param>
public sealed record ConnectResult(ConnectOutcome Outcome, string? Message = null, ChannelConnectionView? Connection = null, string? AuthorizationUrl = null);

/// <summary>Conexões da loja do escopo com os canais (tela Canais de venda — RF21).</summary>
public interface IChannelConnections
{
    Task<ChannelsOverview> OverviewAsync(CancellationToken ct = default);

    /// <summary>Começa a autorização: devolve o endereço da página do ML para onde o navegador vai.</summary>
    Task<ConnectResult> StartMercadoLivreAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Volta do ML (código e state; ou o erro, se a loja negou).</summary>
    Task<ConnectResult> CompleteMercadoLivreAsync(string? code, string? state, string? denial, CancellationToken ct = default);

    /// <summary>Revoga no canal (melhor esforço), apaga os tokens e para a sincronização.</summary>
    Task<bool> DisconnectAsync(Guid connectionId, CancellationToken ct = default);
}

/// <summary>Renova os acessos que vencem em breve na loja do escopo (RF21 CA2). Publicada de hora em hora pelo Worker.</summary>
public sealed record RenewChannelCredentials;
