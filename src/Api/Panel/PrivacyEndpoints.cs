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
        // Direitos do titular e exportação valem mesmo com o painel somente leitura (RF38; HIPÓTESE: a LGPD não espera a fatura).
        var privacy = panel.MapGroup("/privacidade").RequirePermission(Permission.DataExport).AllowWhenPanelReadOnly();

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

        // Exportação completa da loja (RF39): gerada na fila; o download passa por aqui (registrado) e vale 7 dias.
        privacy.MapGet("/exportacoes", async (IDataExports exports, CancellationToken ct) => TypedResults.Ok(await exports.ListAsync(ct)));

        privacy.MapPost("/exportacoes", async Task<Results<Accepted<DataExportView>, ProblemHttpResult>> (IDataExports exports, CancellationToken ct) =>
        {
            var (export, error) = await exports.RequestAsync(ct);
            return export is not null
                ? TypedResults.Accepted((string?)null, export)
                : AuthEndpoints.Problem(StatusCodes.Status409Conflict, error!);
        });

        privacy.MapGet("/exportacoes/{id:guid}/arquivo", async Task<Results<FileStreamHttpResult, ProblemHttpResult>> (
            Guid id, IDataExports exports, HttpResponse response, CancellationToken ct) =>
        {
            if (await exports.DownloadAsync(id, ct) is not { } download)
                return AuthEndpoints.Problem(StatusCodes.Status404NotFound, "Exportação não encontrada ou vencida. Gere uma nova.");
            response.Headers.CacheControl = "no-store";
            return TypedResults.Stream(download.File.Content, download.File.ContentType, download.FileName);
        });

        privacy.MapGet("/acessos", async (string? acao, Guid? pedido, int? pagina, IPersonalDataAccessLog log, CancellationToken ct) =>
            TypedResults.Ok(await log.ListAsync(new PersonalDataAccessFilter(acao, pedido, pagina ?? 1), ct)));
    }
}
