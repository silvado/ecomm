using Ecommerce.Api.Auth;
using Ecommerce.Application.Channels;
using Ecommerce.Domain.Identity;
using Ecommerce.Infrastructure.Channels;
using Ecommerce.Infrastructure.Integrations;
using Microsoft.AspNetCore.Http.HttpResults;
using Wolverine;

namespace Ecommerce.Api.Panel;

public sealed record FakeChannelConnectRequest(string? Conta);
public sealed record FakeChannelPublishRequest(Guid PecaId);

/// <summary>Volta da página de autorização do ML, repassada pelo painel: <c>code</c> e <c>state</c>, ou <c>error</c> se a loja negou.</summary>
public sealed record OAuthReturnRequest(string? Code, string? State, string? Error);

public sealed record AuthorizationStart(string AuthorizationUrl);

/// <summary>Anúncios das peças nos canais (E2): o painel vê a situação e pausa ou retoma (pausa do lojista — RF24 CA2).</summary>
public static class ChannelEndpoints
{
    public static void MapChannelEndpoints(this RouteGroupBuilder panel, IConfiguration configuration)
    {
        // Canais de venda (RF21): conectar, ver e desconectar contas. Só o Dono (dados e contratos da loja).
        var channels = panel.MapGroup("/canais").RequirePermission(Permission.StoreManage);

        channels.MapGet("/", async (IChannelConnections service, CancellationToken ct) => TypedResults.Ok(await service.OverviewAsync(ct)));

        channels.MapPost("/mercado-livre/conectar", async Task<Results<Ok<AuthorizationStart>, ProblemHttpResult>> (
            HttpContext http, IChannelConnections service, CancellationToken ct) =>
        {
            var result = await service.StartMercadoLivreAsync(http.GetPanelUser().UserId, ct);
            return result.AuthorizationUrl is { } url ? TypedResults.Ok(new AuthorizationStart(url)) : ConnectProblem(result);
        });

        channels.MapPost("/mercado-livre/retorno", async Task<Results<Ok<ChannelConnectionView>, ProblemHttpResult>> (
            OAuthReturnRequest request, IChannelConnections service, CancellationToken ct) =>
        {
            var result = await service.CompleteMercadoLivreAsync(request.Code, request.State, request.Error, ct);
            return result.Connection is { } connection ? TypedResults.Ok(connection) : ConnectProblem(result);
        });

        channels.MapDelete("/{id:guid}", async Task<Results<NoContent, ProblemHttpResult>> (Guid id, IChannelConnections service, CancellationToken ct) =>
            await service.DisconnectAsync(id, ct)
                ? TypedResults.NoContent()
                : AuthEndpoints.Problem(StatusCodes.Status404NotFound, "Conexão não encontrada ou já desconectada."));

        panel.MapGet("/pecas/{id:guid}/anuncios", async (Guid id, IChannelQueries queries, CancellationToken ct) =>
            TypedResults.Ok(await queries.ListingsForPartAsync(id, ct))).RequirePermission(Permission.CatalogManage);

        panel.MapPost("/anuncios/{id:guid}/{acao:regex(^(pausar|retomar)$)}", async Task<Results<NoContent, ProblemHttpResult>> (
            Guid id, string acao, HttpContext http, IMessageBus bus, CancellationToken ct) =>
        {
            var result = await bus.InvokeForTenantAsync<ListingCommandResult>(http.GetPanelUser().Access.TenantId.ToString(),
                new ChangeListingByUser(id, acao == "pausar"), ct);
            return result.Outcome switch
            {
                ListingCommandOutcome.Done => TypedResults.NoContent(),
                ListingCommandOutcome.NotFound => AuthEndpoints.Problem(StatusCodes.Status404NotFound, result.Message!),
                _ => AuthEndpoints.Problem(StatusCodes.Status409Conflict, result.Message!),
            };
        }).RequirePermission(Permission.CatalogManage);

        if (!configuration.GetValue<bool>("Channels:UseFakeChannel")) return;


        // Só dev e testes: conectar a loja ao canal simulado e anunciar uma peça nele (a conexão real é o OAuth do RF21).
        var fake = panel.MapGroup("/" + FakeChannelProcessor.ChannelName).RequirePermission(Permission.StoreManage).ExcludeFromDescription();

        fake.MapPost("/conectar", async Task<Results<Ok<Guid>, ProblemHttpResult>> (FakeChannelConnectRequest request, ChannelService channels, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Conta)) return AuthEndpoints.Problem(StatusCodes.Status400BadRequest, "Informe a conta do canal.");
            try
            {
                return TypedResults.Ok((await channels.ConnectAsync(FakeChannelProcessor.ChannelName, request.Conta.Trim(), ct)).Id);
            }
            catch (InvalidOperationException e)
            {
                return AuthEndpoints.Problem(StatusCodes.Status409Conflict, e.Message);
            }
        });

        fake.MapPost("/publicar", async Task<Results<Ok<IReadOnlyList<ListingView>>, ProblemHttpResult>> (
            FakeChannelPublishRequest request, ChannelService channels, IChannelQueries queries, CancellationToken ct) =>
        {
            try
            {
                var connectionId = await channels.FakeConnectionIdAsync(ct)
                    ?? throw new InvalidOperationException("Conecte a loja ao canal simulado antes.");
                await channels.PublishAsync(connectionId, request.PecaId, ct);
                return TypedResults.Ok(await queries.ListingsForPartAsync(request.PecaId, ct));
            }
            catch (InvalidOperationException e)
            {
                return AuthEndpoints.Problem(StatusCodes.Status409Conflict, e.Message);
            }
        });
    }

    private static ProblemHttpResult ConnectProblem(ConnectResult result) => result.Outcome switch
    {
        ConnectOutcome.Unavailable => AuthEndpoints.Problem(StatusCodes.Status503ServiceUnavailable, result.Message!),
        ConnectOutcome.Invalid => AuthEndpoints.Problem(StatusCodes.Status400BadRequest, result.Message!),
        _ => AuthEndpoints.Problem(StatusCodes.Status409Conflict, result.Message!),
    };

    /// <summary>
    /// Só com o ML simulado: a "página de autorização" do Mercado Livre. Autorizar volta ao painel com <c>code=SIM-{vendedor}</c>;
    /// negar volta com <c>error=access_denied</c>, como o ML real.
    /// </summary>
    public static void MapFakeMercadoLivreAuthorization(this IEndpointRouteBuilder app) =>
        app.MapGet(Ecommerce.Infrastructure.Channels.MercadoLivre.FakeMercadoLivreAuth.SimulationPath, (string state, IMercadoLivreAuth auth) =>
        {
            var back = ((Ecommerce.Infrastructure.Channels.MercadoLivre.FakeMercadoLivreAuth)auth).RedirectUri;
            var seller = System.Security.Cryptography.RandomNumberGenerator.GetInt32(100_000_000, 999_999_999);
            var encodedBack = System.Net.WebUtility.HtmlEncode(back);
            var encodedState = System.Net.WebUtility.HtmlEncode(state);
            var deny = System.Net.WebUtility.HtmlEncode($"{back}?error=access_denied&state={Uri.EscapeDataString(state)}");
            return TypedResults.Content($"""
                <!doctype html><html lang="pt-BR"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
                <title>Mercado Livre simulado</title><meta name="robots" content="noindex"></head>
                <body style="font-family:system-ui;max-width:420px;margin:3rem auto;padding:0 1rem">
                <h1>Mercado Livre simulado</h1><p>Ambiente de desenvolvimento: nenhuma conta real é usada.</p>
                <p>A plataforma pede acesso para publicar e gerenciar anúncios, vendas e perguntas da sua conta.</p>
                <form method="get" action="{encodedBack}">
                <input type="hidden" name="state" value="{encodedState}">
                <label>Id do vendedor de teste<br><input name="code" value="SIM-{seller}" style="width:100%"></label>
                <p><button style="width:100%;min-height:48px">Autorizar</button></p>
                </form>
                <p><a href="{deny}">Negar acesso</a></p>
                </body></html>
                """, "text/html; charset=utf-8");
        }).ExcludeFromDescription();
}
