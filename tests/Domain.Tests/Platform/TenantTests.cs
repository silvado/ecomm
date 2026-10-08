using Ecommerce.Domain.Platform;

namespace Ecommerce.Domain.Tests.Platform;

public sealed class TenantTests
{
    private static readonly Cnpj Cnpj = Cnpj.Parse("11222333000181");
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("pecas-do-joao")]
    [InlineData("abc")]
    [InlineData("auto123")]
    public void Slug_valido(string slug) => Assert.True(TenantSlug.IsValid(slug));

    [Theory]
    [InlineData("ab")]              // curto
    [InlineData("-joao")]           // hífen nas pontas
    [InlineData("joao-")]
    [InlineData("pecas--joao")]     // hífen duplo
    [InlineData("Joao")]            // maiúscula
    [InlineData("joão")]            // acento
    [InlineData("www")]             // reservado
    [InlineData("api")]
    [InlineData("admin")]
    public void Slug_invalido(string slug) => Assert.False(TenantSlug.IsValid(slug));

    [Fact]
    public void Novo_tenant_nasce_em_onboarding_com_subdominio_verificado_e_banco_compartilhado()
    {
        var tenant = Tenant.Create("pecas-do-joao", Cnpj, "Peças do João Ltda", "Peças do João", "plataforma.com.br", Now);

        Assert.Equal(TenantStatus.Onboarding, tenant.Status);
        Assert.Equal(Tenant.SharedDatabase, tenant.DatabaseKey);
        var domain = Assert.Single(tenant.Domains);
        Assert.Equal("pecas-do-joao.plataforma.com.br", domain.Host);
        Assert.Equal(DomainKind.PlatformSubdomain, domain.Kind);
        Assert.Equal(DomainVerificationStatus.Verified, domain.VerificationStatus);
        Assert.True(domain.IsPrimary);
    }

    [Theory]
    [InlineData(TenantStatus.Onboarding, false)]
    [InlineData(TenantStatus.Active, true)]
    [InlineData(TenantStatus.ReadOnly, true)]
    [InlineData(TenantStatus.Suspended, false)]
    [InlineData(TenantStatus.Closing, false)]
    [InlineData(TenantStatus.Closed, false)]
    public void Loja_so_atende_ativa_ou_somente_leitura(TenantStatus status, bool available) =>
        Assert.Equal(available, Tenant.IsStorefrontAvailable(status));

    [Theory]
    [InlineData("Loja.Plataforma.com.br", "loja.plataforma.com.br")]
    [InlineData("loja.plataforma.com.br:8443", "loja.plataforma.com.br")]
    [InlineData("www.pecas.com.br.", "www.pecas.com.br")]
    public void Host_e_normalizado(string input, string expected) =>
        Assert.Equal(expected, TenantDomain.NormalizeHost(input));
}
