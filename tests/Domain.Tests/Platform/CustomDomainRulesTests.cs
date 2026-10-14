using Ecommerce.Domain.Platform;

namespace Ecommerce.Domain.Tests.Platform;

/// <summary>RF03: formato do domínio informado, um de cada tipo, domínio principal e janela de 72 h.</summary>
public sealed class CustomDomainRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static Tenant NewTenant() => Tenant.Create("pecas-teste", Cnpj.Parse("11222333000181"), "Peças Ltda", "Peças", "plataforma.com.br", Now);

    [Theory]
    [InlineData("www.MinhaLoja.com.br", "www.minhaloja.com.br", DomainKind.CustomWww)]
    [InlineData("https://www.minhaloja.com.br/", "www.minhaloja.com.br", DomainKind.CustomWww)]
    [InlineData("minhaloja.com.br.", "minhaloja.com.br", DomainKind.CustomApex)]
    [InlineData("loja.empresa.com", "loja.empresa.com", DomainKind.CustomApex)]
    public void Formatos_aceitos(string input, string host, DomainKind kind) =>
        Assert.Equal((host, kind), TenantDomain.ParseCustom(input, "plataforma.com.br"));

    [Theory]
    [InlineData("")]
    [InlineData("minhaloja")]
    [InlineData("www.minha loja.com.br")]
    [InlineData("www.loja.com.br/pecas")]
    [InlineData("www.loja.com.br:8080")]
    [InlineData("-loja.com.br")]
    [InlineData("10.0.0.1")]
    [InlineData("pecas.plataforma.com.br")]
    [InlineData("plataforma.com.br")]
    public void Formatos_recusados(string input) =>
        Assert.Throws<ArgumentException>(() => TenantDomain.ParseCustom(input, "plataforma.com.br"));

    [Fact]
    public void Um_de_cada_tipo_e_codigo_de_verificacao_unico()
    {
        var tenant = NewTenant();
        var www = tenant.AddCustomDomain("www.loja.com.br", DomainKind.CustomWww, Now);
        Assert.Equal(DomainVerificationStatus.Pending, www.VerificationStatus);
        Assert.Equal("_ecomm-verificacao.www.loja.com.br", www.VerificationRecordName);
        Assert.Equal(32, www.VerificationToken!.Length);
        Assert.Throws<InvalidOperationException>(() => tenant.AddCustomDomain("www.outra.com.br", DomainKind.CustomWww, Now));
        var apex = tenant.AddCustomDomain("loja.com.br", DomainKind.CustomApex, Now);
        Assert.NotEqual(www.VerificationToken, apex.VerificationToken);
    }

    [Fact]
    public void Verificado_vira_principal_com_preferencia_pelo_www_e_remover_volta_ao_subdominio()
    {
        var tenant = NewTenant();
        var apex = tenant.AddCustomDomain("loja.com.br", DomainKind.CustomApex, Now);
        var www = tenant.AddCustomDomain("www.loja.com.br", DomainKind.CustomWww, Now);

        tenant.DomainVerified(apex.Id, Now);
        Assert.True(apex.IsPrimary);
        tenant.DomainVerified(www.Id, Now);
        Assert.True(www.IsPrimary);
        Assert.False(apex.IsPrimary);

        tenant.RemoveCustomDomain(www.Id);
        Assert.True(apex.IsPrimary);
        tenant.RemoveCustomDomain(apex.Id);
        Assert.True(tenant.Domains.Single().IsPrimary);
        Assert.Throws<InvalidOperationException>(() => tenant.RemoveCustomDomain(tenant.Domains.Single().Id));
    }

    [Fact]
    public void Depois_de_72_horas_sem_acerto_falha_e_verificar_de_novo_reabre()
    {
        var tenant = NewTenant();
        var www = tenant.AddCustomDomain("www.loja.com.br", DomainKind.CustomWww, Now);
        tenant.DomainCheckFailed(www.Id, "ainda não", Now.AddHours(71));
        Assert.Equal(DomainVerificationStatus.Pending, www.VerificationStatus);
        tenant.DomainCheckFailed(www.Id, "ainda não", Now.AddHours(73));
        Assert.Equal((DomainVerificationStatus.Failed, "ainda não"), (www.VerificationStatus, www.LastCheckMessage));

        tenant.RestartDomainVerification(www.Id, Now.AddHours(80));
        Assert.Equal(DomainVerificationStatus.Pending, www.VerificationStatus);
        Assert.Null(www.LastCheckMessage);
    }
}
