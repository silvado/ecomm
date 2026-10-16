using Ecommerce.Domain.Onboarding;

namespace Ecommerce.Application.Onboarding;

/// <summary>Primeiros passos da loja do escopo (RF05). <c>Published</c> = a loja virtual já está aberta aos compradores.</summary>
public sealed record OnboardingView(bool Published, IReadOnlyList<OnboardingStepState> Steps, bool CanPublish);

public enum PublishOutcome
{
    Published,
    AlreadyPublished,
    /// <summary>Faltam passos obrigatórios (RF05 CA3).</summary>
    Incomplete,
    /// <summary>Loja suspensa ou encerrada: só a plataforma muda a situação.</summary>
    NotAllowed,
}

public sealed record PublishResult(PublishOutcome Outcome, OnboardingView View);

public interface IOnboardingService
{
    Task<OnboardingView> GetAsync(CancellationToken ct = default);

    /// <summary>Pula (ou volta a mostrar) um passo opcional. Obrigatório lança <see cref="ArgumentException"/>.</summary>
    Task<OnboardingView> SetSkippedAsync(OnboardingStep onboardingStep, bool skipped, CancellationToken ct = default);

    /// <summary>RF05 CA3: abre a loja virtual só com empresa, pagamento e frete concluídos (conferido aqui, no servidor).</summary>
    Task<PublishResult> PublishAsync(CancellationToken ct = default);
}
