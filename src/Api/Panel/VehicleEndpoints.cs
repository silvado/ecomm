using Ecommerce.Api.Auth;
using Ecommerce.Application.Catalog;
using Ecommerce.Application.Vehicles;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ecommerce.Api.Panel;

public sealed record CompatibilityRequest(Guid VehicleVersionId, int? YearFrom, int? YearTo);

/// <summary>Tabela de veículos (RF09) e compatibilidades das peças.</summary>
public static class VehicleEndpoints
{
    /// <summary>Seleção em cascata marca → modelo → versão. Usada no painel e na loja (dados da plataforma, sem tenant).</summary>
    public static void MapVehicleCatalog(this RouteGroupBuilder group)
    {
        var vehicles = group.MapGroup("/veiculos");
        vehicles.MapGet("/marcas", async (IVehicleCatalog catalog, CancellationToken ct) => TypedResults.Ok(await catalog.BrandsAsync(ct)));
        vehicles.MapGet("/marcas/{brandId:guid}/modelos", async (Guid brandId, IVehicleCatalog catalog, CancellationToken ct) =>
            TypedResults.Ok(await catalog.ModelsAsync(brandId, ct)));
        vehicles.MapGet("/modelos/{modelId:guid}/versoes", async (Guid modelId, IVehicleCatalog catalog, CancellationToken ct) =>
            TypedResults.Ok(await catalog.VersionsAsync(modelId, includeDiscontinued: false, ct)));
    }

    public static void MapCompatibilityEndpoints(this RouteGroupBuilder panel)
    {
        var compatibilities = panel.MapGroup("/pecas/{id:guid}/compatibilidades").RequirePermission(Domain.Identity.Permission.CatalogManage);

        compatibilities.MapPost("/", async Task<Results<Created<PartView>, ProblemHttpResult>> (
            Guid id, CompatibilityRequest request, IPartCompatibilities service, IPartQueries queries, CancellationToken ct) =>
        {
            var result = await service.AddAsync(id, request.VehicleVersionId, request.YearFrom, request.YearTo, ct);
            if (result.Outcome != CompatibilityOutcome.Done) return Problem(result);
            return TypedResults.Created($"/api/painel/pecas/{id}/compatibilidades/{result.CompatibilityId}", (await queries.GetAsync(id, ct))!);
        });

        compatibilities.MapDelete("/{compatibilityId:guid}", async Task<Results<Ok<PartView>, ProblemHttpResult>> (
            Guid id, Guid compatibilityId, IPartCompatibilities service, IPartQueries queries, CancellationToken ct) =>
        {
            var result = await service.RemoveAsync(id, compatibilityId, ct);
            return result.Outcome == CompatibilityOutcome.Done ? TypedResults.Ok((await queries.GetAsync(id, ct))!) : Problem(result);
        });
    }

    /// <summary>Filtro por veículo vindo da query string: <c>modelo</c> obrigatório para filtrar; <c>versao</c> e <c>ano</c> opcionais.</summary>
    public static VehicleFilter? VehicleFilterFrom(Guid? modelo, Guid? versao, int? ano) =>
        modelo is { } modelId ? new VehicleFilter(modelId, versao, ano) : null;

    private static ProblemHttpResult Problem(CompatibilityResult result) => result.Outcome switch
    {
        CompatibilityOutcome.NotFound => AuthEndpoints.Problem(StatusCodes.Status404NotFound, result.Message ?? "Não encontrado."),
        CompatibilityOutcome.Conflict => AuthEndpoints.Problem(StatusCodes.Status409Conflict, result.Message!),
        _ => AuthEndpoints.Problem(StatusCodes.Status400BadRequest, result.Message ?? "Dados inválidos."),
    };
}
