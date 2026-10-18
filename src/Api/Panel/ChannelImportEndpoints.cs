using Ecommerce.Api.Auth;
using Ecommerce.Application.Channels;
using Ecommerce.Domain.Identity;
using Microsoft.AspNetCore.Http.HttpResults;
using Wolverine;

namespace Ecommerce.Api.Panel;

/// <summary>
/// Importação dos anúncios que a loja já tem no Mercado Livre (RF11 CA2/CA3), em Canais de venda (só o Dono, como a
/// conexão da conta). Começa na hora e roda pela fila; a tela acompanha o progresso pela consulta.
/// </summary>
public static class ChannelImportEndpoints
{
    public static void MapChannelImportEndpoints(this RouteGroupBuilder panel)
    {
        var imports = panel.MapGroup("/canais/mercado-livre/importacoes").RequirePermission(Permission.StoreManage);

        imports.MapPost("/", async Task<Results<Accepted<ChannelImportView>, Ok<ChannelImportView>, ProblemHttpResult>> (
            HttpContext http, IMessageBus bus, IChannelImports service, CancellationToken ct) =>
        {
            var user = http.GetPanelUser();
            var result = await bus.InvokeForTenantAsync<StartChannelImportResult>(user.Access.TenantId.ToString(), new StartChannelImport(user.UserId), ct);
            if (result.ImportId is not { } id || await service.GetAsync(id, ct) is not { } view)
                return AuthEndpoints.Problem(StatusCodes.Status409Conflict, "Conecte a conta do Mercado Livre em Canais de venda antes de importar.");
            return result.Outcome == StartChannelImportOutcome.AlreadyRunning
                ? TypedResults.Ok(view)
                : TypedResults.Accepted($"/api/painel/canais/mercado-livre/importacoes/{id}", view);
        });

        imports.MapGet("/", async (IChannelImports service, CancellationToken ct) => TypedResults.Ok(await service.RecentAsync(5, ct)));

        imports.MapGet("/{id:guid}", async Task<Results<Ok<ChannelImportView>, ProblemHttpResult>> (Guid id, IChannelImports service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } view ? TypedResults.Ok(view) : AuthEndpoints.Problem(StatusCodes.Status404NotFound, "Importação não encontrada."));
    }
}
