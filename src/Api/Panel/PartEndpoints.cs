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

/// <summary>Dados de criação/edição. O código interno só é informado na criação (identifica a peça em etiquetas e planilhas).</summary>
public sealed record PartRequest(
    string? InternalCode, string Title, string? Description, PartCondition Condition, decimal Price,
    int? LengthCm, int? WidthCm, int? HeightCm, int? WeightG, IReadOnlyList<string>? OemCodes, int Quantity)
{
    public PartDetails Details() => new(Title, Description, Condition, Price, LengthCm, WidthCm, HeightCm, WeightG, OemCodes);
}

public sealed record PartStatusRequest(PartStatus Status);

public sealed record PhotoOrderRequest(IReadOnlyList<Guid>? PhotoIds);

/// <summary>Catálogo de peças no painel (RF08): Dono e Operador (permissão de catálogo).</summary>
public static class PartEndpoints
{
    public static void MapPartEndpoints(this RouteGroupBuilder panel)
    {
        var parts = panel.MapGroup("/pecas").RequirePermission(Permission.CatalogManage);

        // "situacao" no mesmo formato do JSON (draft, active, inactive), sem diferenciar maiúsculas.
        parts.MapGet("/", async Task<Results<Ok<PartPage>, ProblemHttpResult>> (
            string? busca, string? situacao, int? pagina, int? tamanho, Guid? modelo, Guid? versao, int? ano, IPartQueries queries, CancellationToken ct) =>
        {
            PartStatus? status = null;
            if (!string.IsNullOrWhiteSpace(situacao))
            {
                if (!Enum.TryParse<PartStatus>(situacao, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                    return AuthEndpoints.Problem(StatusCodes.Status400BadRequest, "Situação inválida: use draft, active ou inactive.");
                status = parsed;
            }
            var vehicle = VehicleEndpoints.VehicleFilterFrom(modelo, versao, ano);
            return TypedResults.Ok(await queries.SearchAsync(new PartSearch(busca, status, pagina ?? 1, tamanho ?? 25, vehicle), ct));
        });

        parts.MapGet("/{id:guid}", async Task<Results<Ok<PartView>, NotFound>> (Guid id, IPartQueries queries, CancellationToken ct) =>
            await queries.GetAsync(id, ct) is { } part ? TypedResults.Ok(part) : TypedResults.NotFound());

        parts.MapPost("/", async Task<Results<Created<PartView>, ProblemHttpResult>> (
            PartRequest request, HttpContext http, IMessageBus bus, IPartQueries queries, CancellationToken ct) =>
        {
            var user = http.GetPanelUser();
            var result = await InvokeAsync(bus, user, new CreatePart(request.InternalCode ?? string.Empty, request.Details(), request.Quantity, user.UserId), ct);
            if (result.Outcome != PartCommandOutcome.Done) return Problem(result);
            var created = (await queries.GetAsync(result.PartId!.Value, ct))!;
            return TypedResults.Created($"/api/painel/pecas/{created.Id}", created);
        });

        parts.MapPut("/{id:guid}", async Task<Results<Ok<PartView>, ProblemHttpResult>> (
            Guid id, PartRequest request, HttpContext http, IMessageBus bus, IPartQueries queries, CancellationToken ct) =>
        {
            var user = http.GetPanelUser();
            var result = await InvokeAsync(bus, user, new UpdatePart(id, request.Details(), request.Quantity, user.UserId), ct);
            return result.Outcome == PartCommandOutcome.Done ? TypedResults.Ok((await queries.GetAsync(id, ct))!) : Problem(result);
        });

        parts.MapPut("/{id:guid}/situacao", async Task<Results<Ok<PartView>, ProblemHttpResult>> (
            Guid id, PartStatusRequest request, HttpContext http, IMessageBus bus, IPartQueries queries, CancellationToken ct) =>
        {
            var result = await InvokeAsync(bus, http.GetPanelUser(), new ChangePartStatus(id, request.Status), ct);
            return result.Outcome == PartCommandOutcome.Done ? TypedResults.Ok((await queries.GetAsync(id, ct))!) : Problem(result);
        });
    }

    /// <summary>Fotos (RF08 CA3): corpo = bytes da imagem (sem multipart); o tipo é reconhecido pelo conteúdo.</summary>
    public static void MapPartPhotoEndpoints(this RouteGroupBuilder panel)
    {
        var photos = panel.MapGroup("/pecas/{id:guid}/fotos").RequirePermission(Permission.CatalogManage);

        photos.MapPost("/", async Task<Results<Created<PartView>, ProblemHttpResult>> (
            Guid id, HttpRequest request, IPartPhotos service, IPartQueries queries, CancellationToken ct) =>
        {
            var result = await service.AddAsync(id, request.Body, ct);
            if (result.Outcome != PhotoOutcome.Done) return PhotoProblem(result);
            return TypedResults.Created($"/api/painel/pecas/{id}/fotos/{result.PhotoId}", (await queries.GetAsync(id, ct))!);
        });

        photos.MapDelete("/{photoId:guid}", async Task<Results<Ok<PartView>, ProblemHttpResult>> (
            Guid id, Guid photoId, IPartPhotos service, IPartQueries queries, CancellationToken ct) =>
        {
            var result = await service.RemoveAsync(id, photoId, ct);
            return result.Outcome == PhotoOutcome.Done ? TypedResults.Ok((await queries.GetAsync(id, ct))!) : PhotoProblem(result);
        });

        photos.MapPut("/ordem", async Task<Results<Ok<PartView>, ProblemHttpResult>> (
            Guid id, PhotoOrderRequest request, IPartPhotos service, IPartQueries queries, CancellationToken ct) =>
        {
            var result = await service.ReorderAsync(id, request.PhotoIds ?? [], ct);
            return result.Outcome == PhotoOutcome.Done ? TypedResults.Ok((await queries.GetAsync(id, ct))!) : PhotoProblem(result);
        });

        // Prévia no painel; o id muda a cada foto, então pode ficar em cache no navegador do lojista.
        photos.MapGet("/{photoId:guid}/{size:int}", async Task<Results<FileStreamHttpResult, NotFound>> (
            Guid id, Guid photoId, int size, HttpResponse response, IPartPhotos service, CancellationToken ct) =>
        {
            if (await service.GetAsync(id, photoId, size, ct) is not { } photo) return TypedResults.NotFound();
            response.Headers.CacheControl = "private, max-age=86400";
            return PanelEndpoints.UploadedImage(response, photo);
        });
    }

    private static ProblemHttpResult PhotoProblem(PhotoResult result) => result.Outcome switch
    {
        PhotoOutcome.NotFound => AuthEndpoints.Problem(StatusCodes.Status404NotFound, "Peça ou foto não encontrada."),
        PhotoOutcome.Conflict => AuthEndpoints.Problem(StatusCodes.Status409Conflict, result.Message!),
        _ => AuthEndpoints.Problem(StatusCodes.Status400BadRequest, result.Message ?? "Imagem inválida."),
    };

    /// <summary>Executa o comando na loja do usuário. Código repetido em corrida chega como violação do índice único.</summary>
    private static async Task<PartCommandResult> InvokeAsync<T>(IMessageBus bus, PanelUser user, T command, CancellationToken ct) where T : notnull
    {
        try
        {
            return await bus.InvokeForTenantAsync<PartCommandResult>(user.Access.TenantId.ToString(), command, ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: PartHandler.InternalCodeIndex,
        })
        {
            return PartCommandResult.Fail(PartCommandOutcome.CodeTaken, PartHandler.CodeTakenMessage);
        }
    }

    private static ProblemHttpResult Problem(PartCommandResult result) => result.Outcome switch
    {
        PartCommandOutcome.NotFound => AuthEndpoints.Problem(StatusCodes.Status404NotFound, "Peça não encontrada."),
        PartCommandOutcome.CodeTaken or PartCommandOutcome.BelowReserved => AuthEndpoints.Problem(StatusCodes.Status409Conflict, result.Message!),
        _ => AuthEndpoints.Problem(StatusCodes.Status400BadRequest, result.Message ?? "Dados inválidos."),
    };
}
