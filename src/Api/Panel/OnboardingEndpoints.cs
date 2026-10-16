using Ecommerce.Api.Auth;
using Ecommerce.Application.Onboarding;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Onboarding;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ecommerce.Api.Panel;

/// <summary>Primeiros passos da loja (RF05): situação de cada passo, pular os opcionais e publicar. Só o Dono.</summary>
public static class OnboardingEndpoints
{
    public static void MapOnboardingEndpoints(this RouteGroupBuilder panel)
    {
        var onboarding = panel.MapGroup("/primeiros-passos").RequirePermission(Permission.StoreManage);

        onboarding.MapGet("/", async (IOnboardingService service, CancellationToken ct) => TypedResults.Ok(await service.GetAsync(ct)));

        onboarding.MapPut("/{passo}/pular", (string passo, IOnboardingService service, CancellationToken ct) => SkipAsync(passo, true, service, ct));
        onboarding.MapDelete("/{passo}/pular", (string passo, IOnboardingService service, CancellationToken ct) => SkipAsync(passo, false, service, ct));

        onboarding.MapPost("/publicar", async Task<Results<Ok<OnboardingView>, ProblemHttpResult>> (IOnboardingService service, CancellationToken ct) =>
        {
            var result = await service.PublishAsync(ct);
            return result.Outcome switch
            {
                PublishOutcome.Published or PublishOutcome.AlreadyPublished => TypedResults.Ok(result.View),
                PublishOutcome.Incomplete => AuthEndpoints.Problem(StatusCodes.Status409Conflict,
                    "Para publicar, conclua os passos obrigatórios: " + string.Join(", ",
                        result.View.Steps.Where(s => s.Required && !s.Done).Select(s => StepName(s.Step))) + "."),
                _ => AuthEndpoints.Problem(StatusCodes.Status409Conflict, "A loja está suspensa ou encerrada. Fale com o suporte."),
            };
        });
    }

    private static async Task<Results<Ok<OnboardingView>, ProblemHttpResult>> SkipAsync(string passo, bool skipped, IOnboardingService service, CancellationToken ct)
    {
        if (!Enum.TryParse<OnboardingStep>(passo, ignoreCase: true, out var step) || !Enum.IsDefined(step))
            return AuthEndpoints.Problem(StatusCodes.Status404NotFound, "Passo não encontrado.");
        try
        {
            return TypedResults.Ok(await service.SetSkippedAsync(step, skipped, ct));
        }
        catch (ArgumentException e)
        {
            return AuthEndpoints.Problem(StatusCodes.Status400BadRequest, e.Message.Split(" (Parameter", 2)[0]);
        }
    }

    private static string StepName(OnboardingStep step) => step switch
    {
        OnboardingStep.Company => "dados da loja",
        OnboardingStep.Payment => "pagamento",
        OnboardingStep.Shipping => "frete",
        _ => step.ToString(),
    };
}
