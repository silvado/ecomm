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

/// <summary>Anúncios das peças nos canais (E2): o painel vê a situação e pausa ou retoma (pausa do lojista — RF24 CA2).</summary>
public static class ChannelEndpoints
{
    public static void MapChannelEndpoints(this RouteGroupBuilder panel, IConfiguration configuration)
    {
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
}
