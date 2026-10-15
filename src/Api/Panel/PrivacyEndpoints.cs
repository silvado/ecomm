using Ecommerce.Api.Auth;
using Ecommerce.Application.Privacy;
using Ecommerce.Domain.Identity;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ecommerce.Api.Panel;

public sealed record DataSubjectRequest(string? Email, string? Documento);

/// <summary>
/// Privacidade (RNF04), só o Dono (permissão de exportação de dados): anonimizar comprador a pedido dele e consultar quem
/// acessou dados pessoais.
/// </summary>
public static class PrivacyEndpoints
{
    public static void MapPrivacyEndpoints(this RouteGroupBuilder panel)
    {
        var privacy = panel.MapGroup("/privacidade").RequirePermission(Permission.DataExport);

        privacy.MapPost("/titular/previa", async Task<Results<Ok<AnonymizationPreview>, ProblemHttpResult>> (
            DataSubjectRequest request, IBuyerAnonymization anonymization, CancellationToken ct) =>
        {
            var (preview, error) = await anonymization.PreviewAsync(new DataSubject(request.Email, request.Documento), ct);
            return preview is not null ? TypedResults.Ok(preview) : AuthEndpoints.Problem(StatusCodes.Status400BadRequest, error!);
        });

        privacy.MapPost("/titular/anonimizar", async Task<Results<Ok<AnonymizationPreview>, ProblemHttpResult>> (
            DataSubjectRequest request, IBuyerAnonymization anonymization, CancellationToken ct) =>
        {
            var result = await anonymization.AnonymizeAsync(new DataSubject(request.Email, request.Documento), ct);
            return result.Preview is not null
                ? TypedResults.Ok(result.Preview)
                : AuthEndpoints.Problem(StatusCodes.Status409Conflict, result.Error!);
        });

        privacy.MapGet("/acessos", async (string? acao, Guid? pedido, int? pagina, IPersonalDataAccessLog log, CancellationToken ct) =>
            TypedResults.Ok(await log.ListAsync(new PersonalDataAccessFilter(acao, pedido, pagina ?? 1), ct)));
    }
}
