using Ecommerce.Domain.Common;
using Ecommerce.Domain.Platform;

namespace Ecommerce.Domain.Onboarding;

/// <summary>Passos do assistente de primeiros passos (RF05), na ordem em que aparecem.</summary>
public enum OnboardingStep
{
    Company,
    Domain,
    Payment,
    Fiscal,
    Shipping,
    MercadoLivre,
    Instagram,
    Olx,
}

/// <summary>Situação de um passo, calculada a partir do que a loja já configurou (nada é duplicado).</summary>
public sealed record OnboardingStepState(OnboardingStep Step, bool Required, bool Done, bool Skipped, string? Detail = null);

/// <summary>O que a loja tem configurado hoje; cada campo vem da tela própria (Dados da loja, Pagamentos, Frete…).</summary>
public sealed record OnboardingFacts(
    bool CompanyReviewed,
    bool CustomDomainVerified,
    bool PaymentConfigured,
    bool PaymentSimulated,
    bool FiscalConfigured,
    bool ShippingConfigured,
    IReadOnlySet<string> ConnectedChannels);

/// <summary>
/// Regras do RF05: empresa, pagamento e frete são obrigatórios para publicar (CA3); os demais podem ser pulados;
/// canais só aparecem se o plano os habilita (CA2). HIPÓTESE: domínio próprio também só aparece se o plano tiver.
/// </summary>
public static class OnboardingRules
{
    public static readonly IReadOnlySet<OnboardingStep> RequiredSteps =
        new HashSet<OnboardingStep> { OnboardingStep.Company, OnboardingStep.Payment, OnboardingStep.Shipping };

    /// <summary>Canal de cada passo de canal (mesmo código das conexões e dos recursos do plano).</summary>
    public static readonly IReadOnlyDictionary<OnboardingStep, string> ChannelOf = new Dictionary<OnboardingStep, string>
    {
        [OnboardingStep.MercadoLivre] = "mercado-livre",
        [OnboardingStep.Instagram] = "meta",
        [OnboardingStep.Olx] = "olx",
    };

    public static bool IsRequired(OnboardingStep step) => RequiredSteps.Contains(step);

    public static bool IsVisible(OnboardingStep step, IReadOnlySet<string> planFeatures) =>
        step switch
        {
            OnboardingStep.Domain => planFeatures.Contains(PlanFeature.CustomDomain),
            _ when ChannelOf.TryGetValue(step, out var channel) => planFeatures.Contains(PlanFeature.ForChannel(channel)),
            _ => true,
        };

    public static IReadOnlyList<OnboardingStepState> Evaluate(OnboardingFacts facts, IReadOnlySet<string> planFeatures, IReadOnlySet<OnboardingStep> skipped) =>
        Enum.GetValues<OnboardingStep>().Where(s => IsVisible(s, planFeatures)).Select(step =>
        {
            var (done, detail) = step switch
            {
                OnboardingStep.Company => (facts.CompanyReviewed, (string?)null),
                OnboardingStep.Domain => (facts.CustomDomainVerified, null),
                OnboardingStep.Payment => (facts.PaymentConfigured || facts.PaymentSimulated,
                    !facts.PaymentConfigured && facts.PaymentSimulated ? "Pagamento simulado (ambiente de teste)." : null),
                OnboardingStep.Fiscal => (facts.FiscalConfigured, null),
                OnboardingStep.Shipping => (facts.ShippingConfigured, null),
                _ => (facts.ConnectedChannels.Contains(ChannelOf[step]), null),
            };
            var required = IsRequired(step);
            return new OnboardingStepState(step, required, done, !done && !required && skipped.Contains(step), detail);
        }).ToList();

    public static bool CanPublish(IEnumerable<OnboardingStepState> steps) => steps.Where(s => s.Required).All(s => s.Done);

    /// <summary>Só os opcionais podem ser pulados.</summary>
    public static bool CanSkip(OnboardingStep step) => !IsRequired(step);
}

/// <summary>Passo opcional que o Dono escolheu pular (o resto da situação vem das configurações da loja).</summary>
public sealed class OnboardingSkip : ITenantOwned
{
    private OnboardingSkip() { }

    public OnboardingSkip(Guid tenantId, OnboardingStep step, DateTimeOffset now)
    {
        if (!OnboardingRules.CanSkip(step)) throw new ArgumentException("Este passo é obrigatório e não pode ser pulado.", nameof(step));
        TenantId = tenantId;
        Step = step;
        SkippedAt = now;
    }

    public Guid TenantId { get; private set; }
    public OnboardingStep Step { get; private set; }
    public DateTimeOffset SkippedAt { get; private set; }
}
