using Ecommerce.Application.Vault;

namespace Ecommerce.Application.Tests.Vault;

/// <summary>RF06 CA5 — textos fixos do aviso do certificado por e-mail.</summary>
public sealed class CertificateReminderTextsTests
{
    [Theory]
    [InlineData(30, "Certificado digital vence em 30 dias — Loja X")]
    [InlineData(5, "Certificado digital vence em 5 dias — Loja X")]
    [InlineData(0, "Certificado digital vence hoje — Loja X")]
    [InlineData(-1, "Certificado digital vencido — Loja X")]
    public void Assunto_conforme_o_dia_de_aviso(int daysLeft, string expected)
    {
        Assert.Equal(expected, CertificateReminderTexts.Subject(daysLeft, "Loja X"));
    }

    [Fact]
    public void Vencido_diz_que_a_NFe_esta_parada()
    {
        Assert.Contains("está parada", CertificateReminderTexts.Headline(-1), StringComparison.Ordinal);
        Assert.Contains("vence em 15 dias", CertificateReminderTexts.Headline(15), StringComparison.Ordinal);
    }
}
