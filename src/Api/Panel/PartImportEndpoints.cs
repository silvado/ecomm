using Ecommerce.Api.Auth;
using Ecommerce.Application.Catalog;
using Ecommerce.Domain.Identity;
using Microsoft.AspNetCore.Http.HttpResults;
using Wolverine;

namespace Ecommerce.Api.Panel;

/// <summary>
/// Importação de peças por planilha (RF11): modelo para baixar, prévia (corpo = bytes do arquivo, nome em "arquivo"),
/// confirmação e acompanhamento do progresso. Dono e Operador (permissão de catálogo).
/// </summary>
public static class PartImportEndpoints
{
    private const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static void MapPartImportEndpoints(this RouteGroupBuilder panel)
    {
        var imports = panel.MapGroup("/pecas/importacoes").RequirePermission(Permission.CatalogManage);

        imports.MapGet("/modelo", Results<FileContentHttpResult, ProblemHttpResult> (string? formato, IPartImports service) =>
            (formato ?? "xlsx").ToLowerInvariant() switch
            {
                "xlsx" => TypedResults.File(service.Template(TemplateFormat.Xlsx), XlsxType, "modelo-importacao-pecas.xlsx"),
                "csv" => TypedResults.File(service.Template(TemplateFormat.Csv), "text/csv; charset=utf-8", "modelo-importacao-pecas.csv"),
                _ => AuthEndpoints.Problem(StatusCodes.Status400BadRequest, "Formato inválido: use xlsx ou csv."),
            });

        imports.MapPost("/", async Task<Results<Created<PartImportView>, ProblemHttpResult>> (
            string? arquivo, HttpRequest request, HttpContext http, IPartImports service, CancellationToken ct) =>
        {
            var result = await service.PreviewAsync(request.Body, arquivo, http.GetPanelUser().UserId, ct);
            return result.Outcome == PreviewOutcome.Validated
                ? TypedResults.Created($"/api/painel/pecas/importacoes/{result.Import!.Id}", result.Import)
                : AuthEndpoints.Problem(StatusCodes.Status400BadRequest, result.Message!);
        });

        imports.MapGet("/", async (IPartImports service, CancellationToken ct) => TypedResults.Ok(await service.RecentAsync(10, ct)));

        imports.MapGet("/{id:guid}", async Task<Results<Ok<PartImportView>, ProblemHttpResult>> (Guid id, IPartImports service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } import
                ? TypedResults.Ok(import)
                : AuthEndpoints.Problem(StatusCodes.Status404NotFound, "Importação não encontrada."));

        // Confirmar de novo (duplo clique) não repete nada: devolve a situação atual.
        imports.MapPost("/{id:guid}/confirmar", async Task<Results<Ok<PartImportView>, ProblemHttpResult>> (
            Guid id, HttpContext http, IMessageBus bus, IPartImports service, CancellationToken ct) =>
        {
            var outcome = await bus.InvokeForTenantAsync<StartPartImportOutcome>(http.GetPanelUser().Access.TenantId.ToString(), new StartPartImport(id), ct);
            return outcome == StartPartImportOutcome.NotFound
                ? AuthEndpoints.Problem(StatusCodes.Status404NotFound, "Importação não encontrada.")
                : TypedResults.Ok((await service.GetAsync(id, ct))!);
        });
    }
}
