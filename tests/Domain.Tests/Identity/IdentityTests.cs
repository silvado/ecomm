using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Platform;

namespace Ecommerce.Domain.Tests.Identity;

public sealed class IdentityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("  Joao@Pecas.com.BR ", "joao@pecas.com.br")]
    [InlineData("ana+loja@x.io", "ana+loja@x.io")]
    public void Email_e_normalizado(string input, string expected) => Assert.Equal(expected, UserAccount.NormalizeEmail(input));

    [Theory]
    [InlineData("sem-arroba")]
    [InlineData("@pecas.com.br")]
    [InlineData("joao@")]
    [InlineData("jo ao@pecas.com.br")]
    [InlineData("a@b@c.com")]
    public void Email_invalido(string input) => Assert.Throws<ArgumentException>(() => UserAccount.NormalizeEmail(input));

    [Fact]
    public void Conta_com_senha_provisoria_libera_apos_troca()
    {
        var user = UserAccount.Create("joao@pecas.com.br", "hash", mustChangePassword: true, Now);

        user.ChangePassword("novo-hash");

        Assert.False(user.MustChangePassword);
        Assert.Equal("novo-hash", user.PasswordHash);
    }

    [Fact]
    public void ToString_nao_expoe_hash_da_senha()
    {
        var user = UserAccount.Create("joao@pecas.com.br", "hash-secreto", mustChangePassword: false, Now);

        Assert.DoesNotContain("hash-secreto", user.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Dono_tem_todas_as_permissoes()
    {
        Assert.Equal(Enum.GetValues<Permission>().ToHashSet(), Permissions.For(TenantRole.Owner).ToHashSet());
    }

    [Theory]
    [InlineData(Permission.CatalogManage, true)]
    [InlineData(Permission.OrdersManage, true)]
    [InlineData(Permission.SupportManage, true)]
    [InlineData(Permission.VaultManage, false)]
    [InlineData(Permission.PlanManage, false)]
    [InlineData(Permission.UsersManage, false)]
    [InlineData(Permission.DataExport, false)]
    public void Operador_sem_cofre_plano_usuarios_e_exportacao(Permission permission, bool allowed) =>
        Assert.Equal(allowed, Permissions.For(TenantRole.Operator).Contains(permission));

    [Theory]
    [InlineData("123456789", false)]
    [InlineData("1234567890", true)]
    [InlineData("          ", false)]
    public void Politica_de_senha(string password, bool valid) => Assert.Equal(valid, PasswordPolicy.IsValid(password));

    [Fact]
    public void Politica_de_senha_recusa_senha_longa_demais() =>
        Assert.False(PasswordPolicy.IsValid(new string('a', PasswordPolicy.MaxLength + 1)));

    [Fact]
    public void Nova_loja_nasce_no_plano_essencial()
    {
        var tenant = Tenant.Create("pecas-do-joao", Cnpj.Parse("11222333000181"), "Peças do João Ltda", "Peças do João", "plataforma.com.br", Now);

        Assert.Equal(Plan.Essential, tenant.PlanCode);
    }
}
