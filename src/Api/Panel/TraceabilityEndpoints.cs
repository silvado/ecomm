using Ecommerce.Api.Auth;
using Ecommerce.Application.Catalog;
using Ecommerce.Domain.Catalog;
using Ecommerce.Domain.Identity;
using Ecommerce.Infrastructure.Catalog;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wolverine;

namespace Ecommerce.Api.Panel;

public sealed record PartTraceabilityRequest(Guid? DonorVehicleId, string? Tag, Dictionary<string, string?>? CustomFields);

public sealed record TraceabilitySettingsRequest(bool DismantlerMode, IReadOnlyList<TraceabilityField>? Fields);

/// <summary>
/// Rastreabilidade de peça usada (RF10), só no painel: veículos doadores e origem das peças (Dono e Operador), modo
/// desmontadora e campos extras (configurados pelo Dono; lidos por quem cadastra peças).
/// </summary>
public static class TraceabilityEndpoints
{
    public static void MapTraceabilityEndpoints(this RouteGroupBuilder panel)
    {
        var donors = panel.MapGroup("/doadores").RequirePermission(Permission.CatalogManage);

        donors.MapGet("/", async (string? busca, int? pagina, int? tamanho, IDonorVehicles service, CancellationToken ct) =>
            TypedResults.Ok(await service.SearchAsync(busca, pagina ?? 1, tamanho ?? 25, ct)));

        donors.MapGet("/{id:guid}", async Task<Results<Ok<DonorVehicleView>, ProblemHttpResult>> (Guid id, IDonorVehicles service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } vehicle ? TypedResults.Ok(vehicle) : NotFound());

        donors.MapPost("/", async Task<Results<Created<DonorVehicleView>, ProblemHttpResult>> (DonorVehicleDetails request, IDonorVehicles service, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(request, ct);
            return result is { Outcome: DonorOutcome.Done, Vehicle: { } created }
                ? TypedResults.Created($"/api/painel/doadores/{created.Id}", created)
                : Problem(result);
        });

        donors.MapPut("/{id:guid}", async Task<Results<Ok<DonorVehicleView>, ProblemHttpResult>> (Guid id, DonorVehicleDetails request, IDonorVehicles service, CancellationToken ct) =>
        {
            var result = await service.UpdateAsync(id, request, ct);
            return result is { Outcome: DonorOutcome.Done, Vehicle: { } updated } ? TypedResults.Ok(updated) : Problem(result);
        });

        donors.MapDelete("/{id:guid}", async Task<Results<NoContent, ProblemHttpResult>> (Guid id, IDonorVehicles service, CancellationToken ct) =>
        {
            var result = await service.DeleteAsync(id, ct);
            return result.Outcome == DonorOutcome.Done ? TypedResults.NoContent() : Problem(result);
        });

        panel.MapGet("/rastreabilidade", async (IDonorVehicles service, CancellationToken ct) => TypedResults.Ok(await service.SettingsAsync(ct)))
            .RequirePermission(Permission.CatalogManage);

        panel.MapPut("/rastreabilidade", async Task<Results<Ok<TraceabilitySettingsView>, ProblemHttpResult>> (
            TraceabilitySettingsRequest request, IDonorVehicles service, CancellationToken ct) =>
        {
            try
            {
                return TypedResults.Ok(await service.SaveSettingsAsync(request.DismantlerMode, request.Fields ?? [], ct));
            }
            catch (ArgumentException e)
            {
                return AuthEndpoints.Problem(StatusCodes.Status400BadRequest, e.Message.Split(" (Parameter", 2)[0]);
            }
        }).RequirePermission(Permission.StoreManage);

        panel.MapPut("/pecas/{id:guid}/rastreabilidade", async Task<Results<Ok<PartView>, ProblemHttpResult>> (
            Guid id, PartTraceabilityRequest request, HttpContext http, IMessageBus bus, IPartQueries queries, CancellationToken ct) =>
        {
            PartCommandResult result;
            try
            {
                result = await bus.InvokeForTenantAsync<PartCommandResult>(http.GetPanelUser().Access.TenantId.ToString(),
                    new SetPartTraceability(id, request.DonorVehicleId, request.Tag, request.CustomFields), ct);
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: PartHandler.TraceabilityTagIndex,
            })
            {
                result = PartCommandResult.Fail(PartCommandOutcome.CodeTaken, PartHandler.TagTakenMessage);
            }
            return result.Outcome switch
            {
                PartCommandOutcome.Done => TypedResults.Ok((await queries.GetAsync(id, ct))!),
                PartCommandOutcome.NotFound => AuthEndpoints.Problem(StatusCodes.Status404NotFound, "Peça não encontrada."),
                PartCommandOutcome.CodeTaken => AuthEndpoints.Problem(StatusCodes.Status409Conflict, result.Message!),
                _ => AuthEndpoints.Problem(StatusCodes.Status400BadRequest, result.Message ?? "Dados inválidos."),
            };
        }).RequirePermission(Permission.CatalogManage);
    }

    private static ProblemHttpResult NotFound() => AuthEndpoints.Problem(StatusCodes.Status404NotFound, "Veículo doador não encontrado.");

    private static ProblemHttpResult Problem(DonorResult result) => result.Outcome switch
    {
        DonorOutcome.NotFound => NotFound(),
        DonorOutcome.Duplicate or DonorOutcome.Conflict => AuthEndpoints.Problem(StatusCodes.Status409Conflict, result.Message!),
        _ => AuthEndpoints.Problem(StatusCodes.Status400BadRequest, result.Message ?? "Dados inválidos."),
    };
}
