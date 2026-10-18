using Ecommerce.Api.Auth;
using Ecommerce.Application.Channels;
using Ecommerce.Domain.Identity;
using Microsoft.AspNetCore.Http.HttpResults;
using Wolverine;

namespace Ecommerce.Api.Panel;

/// <summary>Resposta escrita (ou revisada) pela loja.</summary>
public sealed record AnswerRequest(string? Texto);

public sealed record AutoAnswerRequest(bool Automatica);

/// <summary>
/// Perguntas de compradores (RF31): listar, responder, pedir novo rascunho à IA (atendimento — Dono e Operador) e ligar a
/// resposta automática das simples (só o Dono).
/// </summary>
public static class QuestionEndpoints
{
    public static void MapQuestionEndpoints(this RouteGroupBuilder panel)
    {
        var questions = panel.MapGroup("/perguntas").RequirePermission(Permission.SupportManage);

        questions.MapGet("/", async (string? situacao, IBuyerQuestions service, CancellationToken ct) =>
            TypedResults.Ok(await service.ListAsync(situacao != "todas", ct)));

        questions.MapGet("/configuracao", async (IBuyerQuestions service, CancellationToken ct) => TypedResults.Ok(await service.SettingsAsync(ct)));

        questions.MapPut("/configuracao", async (AutoAnswerRequest request, IBuyerQuestions service, CancellationToken ct) =>
            TypedResults.Ok(await service.SetAutoAnswerAsync(request.Automatica, ct))).RequirePermission(Permission.StoreManage);

        questions.MapPost("/{id:guid}/responder", async Task<Results<Ok<BuyerQuestionView>, ProblemHttpResult>> (
            Guid id, AnswerRequest request, HttpContext http, IMessageBus bus, IBuyerQuestions service, CancellationToken ct) =>
        {
            var user = http.GetPanelUser();
            var result = await bus.InvokeForTenantAsync<AnswerResult>(user.Access.TenantId.ToString(), new SendBuyerAnswer(id, request.Texto ?? string.Empty, user.UserId), ct);
            return result.Outcome switch
            {
                AnswerOutcome.Sent => TypedResults.Ok((await service.GetAsync(id, ct))!),
                AnswerOutcome.NotFound => AuthEndpoints.Problem(StatusCodes.Status404NotFound, result.Message!),
                AnswerOutcome.Invalid => AuthEndpoints.Problem(StatusCodes.Status400BadRequest, result.Message!),
                AnswerOutcome.Refused => AuthEndpoints.Problem(StatusCodes.Status422UnprocessableEntity, result.Message!),
                AnswerOutcome.Unavailable => AuthEndpoints.Problem(StatusCodes.Status503ServiceUnavailable, result.Message!),
                _ => AuthEndpoints.Problem(StatusCodes.Status409Conflict, result.Message!),
            };
        });

        questions.MapPost("/{id:guid}/rascunho", async Task<Results<Ok<BuyerQuestionView>, ProblemHttpResult>> (
            Guid id, HttpContext http, IMessageBus bus, IBuyerQuestions service, CancellationToken ct) =>
        {
            var user = http.GetPanelUser();
            if (await service.GetAsync(id, ct) is null) return AuthEndpoints.Problem(StatusCodes.Status404NotFound, "Pergunta não encontrada.");
            var outcome = await bus.InvokeForTenantAsync<DraftOutcome>(user.Access.TenantId.ToString(), new DraftBuyerAnswer(id, user.UserId), ct);
            return outcome == DraftOutcome.Skipped
                ? AuthEndpoints.Problem(StatusCodes.Status409Conflict, "Esta pergunta já foi respondida ou encerrada.")
                : TypedResults.Ok((await service.GetAsync(id, ct))!);
        });
    }
}
