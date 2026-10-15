using System.Text;
using Ecommerce.Domain.Identity;

namespace Ecommerce.Domain.Tests.Identity;

/// <summary>ADR-0007: TOTP conferido contra os vetores oficiais; um código não vale duas vezes.</summary>
public sealed class TotpTests
{
    /// <summary>Segredo dos vetores SHA1 do apêndice B da RFC 6238.</summary>
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void Confere_com_os_vetores_SHA1_da_RFC_6238(long unixTime, string expected)
    {
        var step = Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(unixTime));

        Assert.Equal(expected, Totp.Compute(RfcSecret, step, digits: 8));
        Assert.Equal(expected[^6..], Totp.Compute(RfcSecret, step));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Base32_confere_com_os_vetores_da_RFC_4648_sem_preenchimento(string input, string expected) =>
        Assert.Equal(expected, Base32.Encode(Encoding.ASCII.GetBytes(input)));

    [Fact]
    public void Aceita_um_passo_de_atraso_ou_adiantamento_e_recusa_dois()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_010);
        var step = Totp.StepAt(now);

        Assert.Equal(step - 1, Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, step - 1), now, null));
        Assert.Equal(step + 1, Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, step + 1), now, null));
        Assert.Null(Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, step - 2), now, null));
        Assert.Null(Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, step + 2), now, null));
    }

    [Fact]
    public void Codigo_ja_usado_ou_anterior_ao_ultimo_aceito_nao_vale()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_010);
        var step = Totp.StepAt(now);
        var code = Totp.Compute(RfcSecret, step);

        Assert.Equal(step, Totp.Verify(RfcSecret, code, now, lastUsedStep: step - 1));
        Assert.Null(Totp.Verify(RfcSecret, code, now, lastUsedStep: step));
        Assert.Null(Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, step - 1), now, lastUsedStep: step));
    }

    [Theory]
    [InlineData("123 456", "123456")]
    [InlineData(" 123456 ", "123456")]
    [InlineData("12345", null)]
    [InlineData("1234567", null)]
    [InlineData("12a456", null)]
    [InlineData("١٢٣٤٥٦", null)]
    [InlineData(null, null)]
    public void So_aceita_seis_digitos(string? input, string? expected) => Assert.Equal(expected, Totp.Normalize(input));

    [Fact]
    public void Uri_de_cadastro_leva_segredo_em_base32_e_parametros_padrao()
    {
        var uri = Totp.ProvisioningUri("Ecomm plataforma", "ana@ecomm.test", Encoding.ASCII.GetBytes("foobar"));

        Assert.Equal("otpauth://totp/Ecomm%20plataforma:ana%40ecomm.test?secret=MZXW6YTBOI&issuer=Ecomm%20plataforma&algorithm=SHA1&digits=6&period=30", uri);
    }
}

public sealed class PlatformAdminTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Nasce_com_senha_provisoria_e_sem_MFA()
    {
        var admin = PlatformAdmin.Create(" Ana@Ecomm.TEST ", "hash", Now);

        Assert.Equal(("ana@ecomm.test", true, false), (admin.Email, admin.MustChangePassword, admin.HasMfa));
    }

    [Fact]
    public void MFA_so_confirma_depois_de_iniciado_e_nao_recomeca_depois_de_ativo()
    {
        var admin = PlatformAdmin.Create("ana@ecomm.test", "hash", Now);
        Assert.Throws<InvalidOperationException>(() => admin.ConfirmMfa(10, Now));

        admin.StartMfaEnrollment([1, 2, 3], "v1");
        admin.ConfirmMfa(10, Now);

        Assert.Equal((true, 10L), (admin.HasMfa, admin.TotpLastStep!.Value));
        Assert.Throws<InvalidOperationException>(() => admin.StartMfaEnrollment([4], "v1"));
        Assert.Throws<InvalidOperationException>(() => admin.ConfirmMfa(11, Now));
    }

    [Fact]
    public void Sessao_vale_por_8_horas_e_revogada_nao_vale()
    {
        var session = new PlatformAdminSession(Guid.NewGuid(), [1], Now);

        Assert.True(session.IsActive(Now.AddHours(8).AddSeconds(-1)));
        Assert.False(session.IsActive(Now.AddHours(8)));
    }
}
