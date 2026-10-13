using Ecommerce.Application.Vault;

namespace Ecommerce.Application.Tests.Vault;

/// <summary>RF06 CA5: faixas do aviso de vencimento do A1 e dias em que o Worker registra no log.</summary>
public sealed class CertificateAlertTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(31.5, null)]
    [InlineData(30.5, CertificateAlertLevel.Notice)]
    [InlineData(15.5, CertificateAlertLevel.Warning)]
    [InlineData(16.5, CertificateAlertLevel.Notice)]
    [InlineData(5.5, CertificateAlertLevel.Urgent)]
    [InlineData(0.2, CertificateAlertLevel.Urgent)]
    [InlineData(-0.1, CertificateAlertLevel.Expired)]
    public void Faixas(double days, CertificateAlertLevel? expected) =>
        Assert.Equal(expected, CertificateAlerts.For(Now.AddDays(days), Now)?.Level);

    [Theory]
    [InlineData(30, true)]
    [InlineData(15, true)]
    [InlineData(5, true)]
    [InlineData(0, true)]
    [InlineData(-1, true)]
    [InlineData(29, false)]
    [InlineData(-2, false)]
    public void Dias_de_lembrete(int daysLeft, bool reminder) => Assert.Equal(reminder, CertificateAlerts.IsReminderDay(daysLeft));
}
