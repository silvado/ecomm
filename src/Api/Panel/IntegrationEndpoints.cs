using System.Globalization;
using Ecommerce.Api.Auth;
using Ecommerce.Application.Integrations;
using Ecommerce.Domain.Identity;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ecommerce.Api.Panel;

/// <summary>
/// Integrações da loja (RF28/RF29): log das chamadas externas (por pedido, peça, canal, situação e período), falhas de
/// processamento em segundo plano e reprocessamento. Dono e Operador (quem atende pedidos).
/// </summary>
public static class IntegrationEndpoints
{
    public static void MapIntegrationEndpoints(this RouteGroupBuilder panel)
    {
        var integrations = panel.MapGroup("/integracoes").RequirePermission(Permission.OrdersManage);

        // "situacao": ok ou falha; "de"/"ate" em AAAA-MM-DD (dias da loja).
        integrations.MapGet("/log", async Task<Results<Ok<IntegrationLogPage>, ProblemHttpResult>> (
            string? canal, string? situacao, Guid? pedido, Guid? peca, string? de, string? ate, int? pagina, int? tamanho,
            IIntegrationQueries queries, CancellationToken ct) =>
        {
            bool? success = situacao?.ToLowerInvariant() switch
            {
                null or "" => null,
                "ok" => true,
                "falha" => false,
                _ => default(bool?),
            };
            if (situacao is { Length: > 0 } && success is null) return AuthEndpoints.Problem(StatusCodes.Status400BadRequest, "Situação inválida: use ok ou falha.");
            if (!TryDate(de, out var from) || !TryDate(ate, out var to))
                return AuthEndpoints.Problem(StatusCodes.Status400BadRequest, "Data inválida: use AAAA-MM-DD.");
            return TypedResults.Ok(await queries.SearchAsync(new IntegrationLogSearch(canal, success, pedido, peca, from, to, pagina ?? 1, tamanho ?? 50), ct));
        });

        integrations.MapGet("/falhas", async (bool? pendentes, IIntegrationQueries queries, CancellationToken ct) =>
            TypedResults.Ok(await queries.FailuresAsync(pendentes ?? true, ct)));

        integrations.MapGet("/resumo", async (IIntegrationQueries queries, CancellationToken ct) => TypedResults.Ok(await queries.SummaryAsync(ct)));

        integrations.MapPost("/falhas/{id:guid}/reprocessar", async Task<Results<NoContent, ProblemHttpResult>> (
            Guid id, HttpContext http, IIntegrationQueries queries, CancellationToken ct) =>
            await queries.ReplayAsync(id, http.GetPanelUser().UserId, ct) switch
            {
                ReplayOutcome.Replayed => TypedResults.NoContent(),
                ReplayOutcome.AlreadyReplayed => AuthEndpoints.Problem(StatusCodes.Status409Conflict, "Esta falha já foi mandada reprocessar."),
                _ => AuthEndpoints.Problem(StatusCodes.Status404NotFound, "Falha não encontrada."),
            });
    }

    private static bool TryDate(string? value, out DateOnly? date)
    {
        date = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return false;
        date = parsed;
        return true;
    }
}
