using Ecommerce.Domain.Billing;
using Ecommerce.Domain.Platform;

namespace Ecommerce.Domain.Tests.Billing;

/// <summary>RF38 CA2/CA3 — régua de inadimplência: estágio pelo atraso e efeito na situação da loja.</summary>
public sealed class DunningTests
{
    private static readonly DateOnly Today = new(2026, 11, 30);
    private static readonly DateTimeOffset Now = new(2026, 11, 30, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, DunningStage.None)]
    [InlineData(2, DunningStage.None)]
    [InlineData(3, DunningStage.Warned)]
    [InlineData(9, DunningStage.Warned)]
    [InlineData(10, DunningStage.ReadOnly)]
    [InlineData(19, DunningStage.ReadOnly)]
    [InlineData(20, DunningStage.Suspended)]
    [InlineData(59, DunningStage.Suspended)]
    [InlineData(60, DunningStage.Closing)]
    public void Estagio_pelos_dias_de_atraso(int daysLate, DunningStage expected) =>
        Assert.Equal(expected, DunningPolicy.StageFor(Today.AddDays(-daysLate), Today));

    [Fact]
    public void Em_dia_ou_sem_fatura_vencida_nao_tem_estagio()
    {
        Assert.Equal(DunningStage.None, DunningPolicy.StageFor(null, Today));
        Assert.Equal(DunningStage.None, DunningPolicy.StageFor(Today.AddDays(5), Today));
    }

    private static Tenant Published()
    {
        var tenant = Tenant.Create("pecas-teste", Cnpj.Parse("11222333000181"), "Peças Teste Ltda", "Peças Teste", "plataforma.test", Now);
        tenant.Publish();
        return tenant;
    }

    [Fact]
    public void Regua_bloqueia_por_etapas_e_o_pagamento_libera()
    {
        var tenant = Published();

        Assert.False(tenant.ApplyBillingStage(DunningStage.Warned));
        Assert.Equal(TenantStatus.Active, tenant.Status);
        Assert.True(tenant.ApplyBillingStage(DunningStage.ReadOnly));
        Assert.Equal((TenantStatus.ReadOnly, true), (tenant.Status, tenant.BillingHold));
        Assert.True(tenant.ApplyBillingStage(DunningStage.Suspended));
        Assert.True(tenant.ApplyBillingStage(DunningStage.Closing));
        Assert.Equal(TenantStatus.Closing, tenant.Status);

        Assert.True(tenant.ApplyBillingStage(DunningStage.None));
        Assert.Equal((TenantStatus.Active, false), (tenant.Status, tenant.BillingHold));
    }

    [Fact]
    public void Suspensao_da_equipe_prevalece_sobre_a_regua()
    {
        var tenant = Published();
        tenant.ApplyBillingStage(DunningStage.ReadOnly);
        tenant.Suspend();

        Assert.False(tenant.BillingHold);
        Assert.False(tenant.ApplyBillingStage(DunningStage.None));
        Assert.False(tenant.ApplyBillingStage(DunningStage.Closing));
        Assert.Equal(TenantStatus.Suspended, tenant.Status);
    }

    [Fact]
    public void Loja_em_implantacao_nao_entra_na_regua()
    {
        var tenant = Tenant.Create("pecas-nova", Cnpj.Parse("11222333000181"), "Peças Nova Ltda", "Peças Nova", "plataforma.test", Now);

        Assert.False(tenant.ApplyBillingStage(DunningStage.Suspended));
        Assert.Equal(TenantStatus.Onboarding, tenant.Status);
    }

    [Theory]
    [InlineData(TenantStatus.Active, false, true)]
    [InlineData(TenantStatus.ReadOnly, true, true)]
    [InlineData(TenantStatus.Suspended, true, false)]
    [InlineData(TenantStatus.Closing, true, false)]
    [InlineData(TenantStatus.Onboarding, false, true)]
    public void Painel_e_canais_por_situacao(TenantStatus status, bool readOnly, bool selling)
    {
        Assert.Equal(readOnly, Tenant.IsPanelReadOnly(status));
        Assert.Equal(selling, Tenant.IsSellingOnChannels(status));
    }
}
