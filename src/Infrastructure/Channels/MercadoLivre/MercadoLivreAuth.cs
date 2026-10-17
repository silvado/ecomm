using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Ecommerce.Application.Channels;
using Microsoft.Extensions.Options;

namespace Ecommerce.Infrastructure.Channels.MercadoLivre;

/// <summary>
/// App da plataforma no Mercado Livre (ADR-0009). Segredo da plataforma: vem de variável de ambiente
/// (<c>MercadoLivre__ClientSecret</c>), nunca do appsettings.
/// </summary>
public sealed class MercadoLivreOptions
{
    public const string Section = "MercadoLivre";

    /// <summary>Dev e testes: autorização e API simuladas (a API recusa subir assim em produção).</summary>
    public bool UseFake { get; set; }

    /// <summary>APP ID do app criado no DevCenter.</summary>
    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>
    /// Endereço de retorno cadastrado no app: a tela do painel que conclui a conexão
    /// (ex.: https://painel.exemplo.com.br/canais/mercado-livre/retorno). Tem de ser HTTPS e fixo.
    /// </summary>
    public string? RedirectUri { get; set; }

    public string AuthorizationUrl { get; set; } = "https://auth.mercadolivre.com.br/authorization";

    public string ApiUrl { get; set; } = "https://api.mercadolibre.com";

    /// <summary>Renova o acesso quando falta menos que isto para vencer (o acesso dura 6 h).</summary>
    public TimeSpan RenewBefore { get; set; } = TimeSpan.FromMinutes(90);

    public bool IsConfigured => UseFake || (!string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret) && !string.IsNullOrWhiteSpace(RedirectUri));
}

/// <summary>
/// OAuth do Mercado Livre pela API oficial. Fonte (consultada em 16/10/2026, página atualizada em 29/12/2025):
/// https://developers.mercadolivre.com.br/pt_br/autenticacao-e-autorizacao — authorization code com PKCE; troca e
/// renovação em <c>POST /oauth/token</c> (form-urlencoded); acesso de 6 h; refresh token de uso único, válido por 6 meses,
/// e só o último vale; <c>invalid_grant</c> = código ou refresh inválido, vencido ou revogado. Revogação:
/// https://developers.mercadolivre.com.br/pt_br/gerencie-seu-aplicativo (<c>DELETE /users/{id}/applications/{app}</c>).
/// </summary>
public sealed class MercadoLivreAuthClient(HttpClient http, IOptions<MercadoLivreOptions> options, TimeProvider clock) : IMercadoLivreAuth
{
    private MercadoLivreOptions Options => options.Value;

    public bool Configured => Options.IsConfigured;

    public string AuthorizationUrl(string state, string codeChallenge) =>
        $"{Options.AuthorizationUrl}?response_type=code&client_id={Uri.EscapeDataString(Options.ClientId!)}"
        + $"&redirect_uri={Uri.EscapeDataString(Options.RedirectUri!)}&state={Uri.EscapeDataString(state)}"
        + $"&code_challenge={Uri.EscapeDataString(codeChallenge)}&code_challenge_method=S256";

    public Task<OAuthTokens> ExchangeCodeAsync(string code, string codeVerifier, CancellationToken ct = default) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = Options.ClientId!,
            ["client_secret"] = Options.ClientSecret!,
            ["code"] = code,
            ["redirect_uri"] = Options.RedirectUri!,
            ["code_verifier"] = codeVerifier,
        }, ct);

    public Task<OAuthTokens> RefreshAsync(string refreshToken, CancellationToken ct = default) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = Options.ClientId!,
            ["client_secret"] = Options.ClientSecret!,
            ["refresh_token"] = refreshToken,
        }, ct);

    public async Task<string?> AccountNameAsync(string accessToken, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Options.ApiUrl}/users/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        return (await response.Content.ReadFromJsonAsync<UserResponse>(ct))?.Nickname;
    }

    public async Task RevokeAsync(string accessToken, string userId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete,
            $"{Options.ApiUrl}/users/{Uri.EscapeDataString(userId)}/applications/{Uri.EscapeDataString(Options.ClientId!)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await SendAsync(request, ct);
        // Já revogado do lado do ML (401/403/404) também serve: o objetivo é a conta sem o app.
        if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new ChannelConnectorException($"O Mercado Livre não respondeu ({(int)response.StatusCode}).");
    }

    private async Task<OAuthTokens> TokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Options.ApiUrl}/oauth/token") { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
        {
            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(ct);
            if (token is not { AccessToken.Length: > 0, RefreshToken.Length: > 0, UserId: > 0 })
                throw new ChannelConnectorException("Resposta de token do Mercado Livre incompleta.");
            return new OAuthTokens(token.AccessToken, token.RefreshToken, clock.GetUtcNow().AddSeconds(token.ExpiresIn), token.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var error = await ErrorAsync(response, ct);
        // Recusas definitivas: só uma nova autorização resolve. A descrição do ML não vai para log (pode ecoar dados).
        if (error is "invalid_grant" or "unauthorized_client" or "invalid_operator_user_id")
            throw new ChannelAuthorizationRevokedException(error == "invalid_operator_user_id"
                ? "A conta usada é de colaborador: entre com a conta principal (administrador) do Mercado Livre."
                : "O Mercado Livre recusou o acesso (autorização vencida, revogada ou senha alterada). Conecte a conta de novo.");
        if (error is "invalid_client" or "unauthorized_application")
            throw new ChannelConnectorException("O app da plataforma no Mercado Livre está com credenciais inválidas ou bloqueado. A equipe da plataforma foi avisada.");
        throw new ChannelConnectorException($"O Mercado Livre não concluiu o pedido de acesso ({(int)response.StatusCode}{(error is null ? "" : ", " + error)}).");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await http.SendAsync(request, ct);
        }
        catch (HttpRequestException e)
        {
            throw new ChannelConnectorException("Não foi possível falar com o Mercado Livre.", e);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new ChannelConnectorException("O Mercado Livre demorou demais para responder.", e);
        }
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return (await response.Content.ReadFromJsonAsync<ErrorResponse>(ct))?.Error;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("user_id")] long UserId);

    private sealed record ErrorResponse([property: JsonPropertyName("error")] string? Error);

    private sealed record UserResponse([property: JsonPropertyName("nickname")] string? Nickname);
}

/// <summary>
/// Mercado Livre simulado (dev e testes). A "página de autorização" é <see cref="SimulationPath"/> na própria API; o código
/// é <c>SIM-{id do vendedor}</c>. <see cref="Revoke"/> simula a loja tirando o acesso no site do ML (a próxima renovação falha).
/// </summary>
public sealed class FakeMercadoLivreAuth(IOptions<MercadoLivreOptions> options, TimeProvider clock) : IMercadoLivreAuth
{
    public const string SimulationPath = "/api/dev/mercado-livre/autorizar";
    public const string CodePrefix = "SIM-";
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(6);

    // Último refresh token válido por vendedor (uso único, como no ML) e vendedores que revogaram o acesso.
    private static readonly ConcurrentDictionary<string, string> CurrentRefresh = new();
    private static readonly ConcurrentDictionary<string, bool> Revoked = new();
    private static readonly ConcurrentDictionary<string, int> Down = new();

    public bool Configured => true;

    /// <summary>Endereço de retorno usado pela página simulada (o painel; em dev, o ng serve).</summary>
    public string RedirectUri => options.Value.RedirectUri ?? "http://localhost:4200/canais/mercado-livre/retorno";

    public string AuthorizationUrl(string state, string codeChallenge) =>
        $"{SimulationPath}?state={Uri.EscapeDataString(state)}&code_challenge={Uri.EscapeDataString(codeChallenge)}";

    public static void Revoke(string userId) => Revoked[userId] = true;

    /// <summary>As próximas <paramref name="count"/> chamadas para o vendedor falham como ML fora do ar.</summary>
    public static void FailNext(string userId, int count) => Down[userId] = count;

    public Task<OAuthTokens> ExchangeCodeAsync(string code, string codeVerifier, CancellationToken ct = default)
    {
        if (!code.StartsWith(CodePrefix, StringComparison.Ordinal) || code.Length <= CodePrefix.Length || !code[CodePrefix.Length..].All(char.IsAsciiDigit))
            throw new ChannelAuthorizationRevokedException("O Mercado Livre recusou o acesso (autorização vencida, revogada ou senha alterada). Conecte a conta de novo.");
        var userId = code[CodePrefix.Length..];
        ThrowIfDown(userId);
        Revoked.TryRemove(userId, out _);
        return Task.FromResult(Issue(userId));
    }

    public Task<OAuthTokens> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var userId = refreshToken.Split('-').Last();
        ThrowIfDown(userId);
        // Uso único como no ML, dentro deste processo (no compose, API e Worker não compartilham a memória: um refresh
        // emitido pelo outro processo é aceito).
        if (Revoked.ContainsKey(userId) || !refreshToken.StartsWith("SIM-RT-", StringComparison.Ordinal)
            || (CurrentRefresh.TryGetValue(userId, out var current) && current != refreshToken))
            throw new ChannelAuthorizationRevokedException("O Mercado Livre recusou o acesso (autorização vencida, revogada ou senha alterada). Conecte a conta de novo.");
        return Task.FromResult(Issue(userId));
    }

    public Task<string?> AccountNameAsync(string accessToken, CancellationToken ct = default) =>
        Task.FromResult<string?>("VENDEDOR_TESTE_" + accessToken.Split('-').Last());

    public Task RevokeAsync(string accessToken, string userId, CancellationToken ct = default)
    {
        ThrowIfDown(userId);
        Revoke(userId);
        return Task.CompletedTask;
    }

    private OAuthTokens Issue(string userId)
    {
        var refresh = $"SIM-RT-{Guid.NewGuid():N}-{userId}";
        CurrentRefresh[userId] = refresh;
        return new OAuthTokens($"SIM-AT-{Guid.NewGuid():N}-{userId}", refresh, clock.GetUtcNow() + TokenLifetime, userId);
    }

    private static void ThrowIfDown(string userId)
    {
        while (Down.TryGetValue(userId, out var left) && left > 0)
            if (Down.TryUpdate(userId, left - 1, left)) throw new ChannelConnectorException("Mercado Livre simulado fora do ar.");
    }
}
