using System.Net;
using System.Text;
using Ecommerce.Application.Channels;
using Ecommerce.Infrastructure.Channels.MercadoLivre;
using Microsoft.Extensions.Options;

namespace Ecommerce.Integration.Tests.Channels;

/// <summary>
/// Contrato do OAuth do Mercado Livre contra a API simulada, no formato de
/// https://developers.mercadolivre.com.br/pt_br/autenticacao-e-autorizacao (regra 8 do CLAUDE.md).
/// </summary>
public sealed class MercadoLivreAuthContractTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private const string TokenResponse = """
        {"access_token":"APP_USR-123456-090515-8cc4448aac10d5105474e1351-1234567","token_type":"bearer","expires_in":21600,
         "scope":"offline_access read write","user_id":1234567,"refresh_token":"TG-5b9032b4e23464aed1f959f-1234567"}
        """;

    private static (MercadoLivreAuthClient Client, StubHandler Handler) Client(HttpStatusCode status, string body)
    {
        var handler = new StubHandler(status, body);
        var options = Options.Create(new MercadoLivreOptions
        {
            ClientId = "1620218256833906",
            ClientSecret = "segredo-do-app",
            RedirectUri = "https://painel.plataforma.test/canais/mercado-livre/retorno",
            ApiUrl = "https://api.ml.test",
        });
        return (new MercadoLivreAuthClient(new HttpClient(handler), options, TimeProvider.System), handler);
    }

    private static Dictionary<string, string> Form(string body) =>
        body.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));

    [Fact]
    public void Pagina_de_autorizacao_do_Brasil_com_state_e_PKCE_S256()
    {
        var (client, _) = Client(HttpStatusCode.OK, "{}");

        var url = new Uri(client.AuthorizationUrl("estado/aleatorio", "desafio"));

        Assert.Equal("https://auth.mercadolivre.com.br/authorization", url.GetLeftPart(UriPartial.Path));
        var query = Form(url.Query.TrimStart('?'));
        Assert.Equal(("code", "1620218256833906", "https://painel.plataforma.test/canais/mercado-livre/retorno", "estado/aleatorio", "desafio", "S256"),
            (query["response_type"], query["client_id"], query["redirect_uri"], query["state"], query["code_challenge"], query["code_challenge_method"]));
    }

    [Fact]
    public async Task Troca_o_codigo_por_tokens_com_formulario_e_verificador()
    {
        var (client, handler) = Client(HttpStatusCode.OK, TokenResponse);

        var tokens = await client.ExchangeCodeAsync("TG-61828b7fffcc9a001b4bc890-314029626", "verificador");

        Assert.Equal(("APP_USR-123456-090515-8cc4448aac10d5105474e1351-1234567", "TG-5b9032b4e23464aed1f959f-1234567", "1234567"),
            (tokens.AccessToken, tokens.RefreshToken, tokens.UserId));
        Assert.InRange(tokens.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(21590), DateTimeOffset.UtcNow.AddSeconds(21610));
        var (request, body) = handler.Calls.Single();
        Assert.Equal((HttpMethod.Post, "https://api.ml.test/oauth/token", "application/x-www-form-urlencoded"),
            (request.Method, request.RequestUri!.ToString(), request.Content!.Headers.ContentType!.MediaType));
        var form = Form(body!);
        Assert.Equal(("authorization_code", "1620218256833906", "segredo-do-app", "TG-61828b7fffcc9a001b4bc890-314029626", "verificador"),
            (form["grant_type"], form["client_id"], form["client_secret"], form["code"], form["code_verifier"]));
        Assert.Equal("https://painel.plataforma.test/canais/mercado-livre/retorno", form["redirect_uri"]);
    }

    [Fact]
    public async Task Renova_com_o_refresh_token()
    {
        var (client, handler) = Client(HttpStatusCode.OK, TokenResponse);

        await client.RefreshAsync("TG-antigo-1234567");

        var form = Form(handler.Calls.Single().Body!);
        Assert.Equal(("refresh_token", "TG-antigo-1234567"), (form["grant_type"], form["refresh_token"]));
        Assert.False(form.ContainsKey("code"));
    }

    [Theory]
    [InlineData("invalid_grant")]
    [InlineData("unauthorized_client")]
    public async Task Grant_recusado_pede_nova_autorizacao(string error)
    {
        var (client, _) = Client(HttpStatusCode.BadRequest,
            $$"""{"error_description":"Error validating grant. TG-5b9032b4e23464aed1f959f","error":"{{error}}","status":400,"cause":[]}""");

        var e = await Assert.ThrowsAsync<ChannelAuthorizationRevokedException>(() => client.RefreshAsync("TG-segredo-1234567"));

        Assert.Contains("Conecte a conta de novo", e.Message, StringComparison.Ordinal);
        // Nem o refresh enviado nem o que o ML ecoa vão para a mensagem (que vai para o log de integração).
        Assert.DoesNotContain("TG-", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Conta_de_colaborador_tem_mensagem_propria()
    {
        var (client, _) = Client(HttpStatusCode.BadRequest, """{"error":"invalid_operator_user_id","status":400}""");

        var e = await Assert.ThrowsAsync<ChannelAuthorizationRevokedException>(() => client.ExchangeCodeAsync("TG-x", "v"));

        Assert.Contains("conta principal", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"invalid_client"}""")]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":"local_rate_limited"}""")]
    [InlineData(HttpStatusCode.InternalServerError, "<html>erro</html>")]
    public async Task Credencial_do_app_limite_ou_ML_fora_do_ar_nao_derrubam_a_conexao(HttpStatusCode status, string body)
    {
        var (client, _) = Client(status, body);

        var e = await Assert.ThrowsAsync<ChannelConnectorException>(() => client.RefreshAsync("TG-segredo-1234567"));

        Assert.DoesNotContain("segredo", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apelido_e_revogacao_usam_o_token_no_cabecalho()
    {
        var (client, handler) = Client(HttpStatusCode.OK, """{"id":1234567,"nickname":"AUTOPECAS_PILOTO"}""");

        Assert.Equal("AUTOPECAS_PILOTO", await client.AccountNameAsync("APP_USR-abc"));
        await client.RevokeAsync("APP_USR-abc", "1234567");

        Assert.All(handler.Calls, c => Assert.Equal(("Bearer", "APP_USR-abc"), (c.Request.Headers.Authorization!.Scheme, c.Request.Headers.Authorization.Parameter)));
        Assert.Equal((HttpMethod.Get, "https://api.ml.test/users/me"), (handler.Calls[0].Request.Method, handler.Calls[0].Request.RequestUri!.ToString()));
        Assert.Equal((HttpMethod.Delete, "https://api.ml.test/users/1234567/applications/1620218256833906"),
            (handler.Calls[1].Request.Method, handler.Calls[1].Request.RequestUri!.ToString()));
    }

    [Fact]
    public async Task Revogar_conta_que_ja_tirou_o_app_nao_e_erro()
    {
        var (client, _) = Client(HttpStatusCode.NotFound, """{"message":"not found"}""");

        await client.RevokeAsync("APP_USR-abc", "1234567");
    }
}
