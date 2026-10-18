using Ecommerce.Api.Auth;
using Ecommerce.Application.Reports;
using Ecommerce.Domain.Identity;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ecommerce.Api.Panel;

public sealed record DailySummaryRequest(bool Ativo, int Hora);

/// <summary>Resumo diário (RF34) em Dados da loja: horário, ligar/desligar e prévia dos números de ontem (só o Dono).</summary>
public static class DailySummaryEndpoints
{
    public static void MapDailySummaryEndpoints(this RouteGroupBuilder panel)
    {
        var summary = panel.MapGroup("/resumo-diario").RequirePermission(Permission.StoreManage);

        summary.MapGet("/", async (IDailySummaries service, CancellationToken ct) => TypedResults.Ok(await service.SettingsAsync(ct)));

        summary.MapPut("/", async Task<Results<Ok<DailySummarySettingsView>, ProblemHttpResult>> (DailySummaryRequest request, IDailySummaries service, CancellationToken ct) =>
            request.Hora is < 0 or > 23
                ? AuthEndpoints.Problem(StatusCodes.Status400BadRequest, "Escolha uma hora entre 0 e 23.")
                : TypedResults.Ok(await service.SaveAsync(request.Ativo, request.Hora, ct)));

        summary.MapGet("/previa", async (IDailySummaries service, CancellationToken ct) => TypedResults.Ok(await service.PreviewAsync(ct)));
    }
}
