using Ecommerce.Domain.Onboarding;
using Ecommerce.Domain.Platform;

namespace Ecommerce.Domain.Tests.Platform;

/// <summary>RF05: passos visíveis pelo plano, obrigatórios para publicar e pulos só nos opcionais.</summary>
public sealed class OnboardingRulesTests
{
    private static readonly HashSet<string> Essential = [PlanFeature.MercadoLivre];
    private static readonly HashSet<string> Complete = [PlanFeature.MercadoLivre, PlanFeature.Meta, PlanFeature.Olx, PlanFeature.CustomDomain];

    private static OnboardingFacts Facts(bool company = false, bool payment = false, bool simulated = false, bool shipping = false,
        params string[] channels) =>
        new(company, false, payment, simulated, false, shipping, channels.ToHashSet());

    [Fact]
    public void Plano_define_os_passos_de_canal_e_dominio()
    {
        Assert.Equal([OnboardingStep.Company, OnboardingStep.Payment, OnboardingStep.Fiscal, OnboardingStep.Shipping, OnboardingStep.MercadoLivre],
            OnboardingRules.Evaluate(Facts(), Essential, new HashSet<OnboardingStep>()).Select(s => s.Step));
        Assert.Equal(Enum.GetValues<OnboardingStep>(), OnboardingRules.Evaluate(Facts(), Complete, new HashSet<OnboardingStep>()).Select(s => s.Step));
    }

    [Fact]
    public void Publicar_exige_empresa_pagamento_e_frete()
    {
        var none = new HashSet<OnboardingStep>();
        Assert.False(OnboardingRules.CanPublish(OnboardingRules.Evaluate(Facts(company: true, payment: true), Essential, none)));
        Assert.False(OnboardingRules.CanPublish(OnboardingRules.Evaluate(Facts(company: true, shipping: true), Essential, none)));
        Assert.True(OnboardingRules.CanPublish(OnboardingRules.Evaluate(Facts(company: true, payment: true, shipping: true), Essential, none)));
    }

    [Fact]
    public void Pagamento_simulado_conta_e_avisa()
    {
        var payment = OnboardingRules.Evaluate(Facts(simulated: true), Essential, new HashSet<OnboardingStep>()).Single(s => s.Step == OnboardingStep.Payment);

        Assert.True(payment.Done);
        Assert.Contains("simulado", payment.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Pulado_so_vale_para_opcional_ainda_nao_feito()
    {
        var skipped = new HashSet<OnboardingStep> { OnboardingStep.MercadoLivre, OnboardingStep.Shipping };

        var steps = OnboardingRules.Evaluate(Facts(), Essential, skipped);
        Assert.True(steps.Single(s => s.Step == OnboardingStep.MercadoLivre).Skipped);
        Assert.False(steps.Single(s => s.Step == OnboardingStep.Shipping).Skipped);

        var connected = OnboardingRules.Evaluate(Facts(channels: "mercado-livre"), Essential, skipped).Single(s => s.Step == OnboardingStep.MercadoLivre);
        Assert.Equal((true, false), (connected.Done, connected.Skipped));

        Assert.Throws<ArgumentException>(() => new OnboardingSkip(Guid.NewGuid(), OnboardingStep.Payment, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void So_loja_em_implantacao_e_publicada()
    {
        var tenant = Tenant.Create("pecas-ob", Cnpj.Parse("11222333000181"), "Peças Ltda", "Peças", "plataforma.test", DateTimeOffset.UtcNow);

        tenant.Publish();
        Assert.Equal(TenantStatus.Active, tenant.Status);
        Assert.Throws<InvalidOperationException>(tenant.Publish);
        tenant.Suspend();
        Assert.Throws<InvalidOperationException>(tenant.Publish);
    }
}
