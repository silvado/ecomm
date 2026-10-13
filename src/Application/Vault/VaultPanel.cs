namespace Ecommerce.Application.Vault;

/// <summary>Certificado digital A1 da loja (RF06): só os dados públicos — nunca o arquivo nem a senha.</summary>
public sealed record CertificateView(string Holder, string? Cnpj, DateTimeOffset NotBefore, DateTimeOffset ExpiresAt, int DaysLeft,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Aviso de vencimento do A1 (RF06 CA5): a partir de 30 dias antes. <paramref name="Level"/> sobe aos 15 e aos 5 dias e
/// fica "expired" depois de vencer.
/// </summary>
public sealed record CertificateAlert(DateTimeOffset ExpiresAt, int DaysLeft, CertificateAlertLevel Level);

public enum CertificateAlertLevel
{
    /// <summary>30 dias ou menos.</summary>
    Notice,
    /// <summary>15 dias ou menos.</summary>
    Warning,
    /// <summary>5 dias ou menos.</summary>
    Urgent,
    Expired,
}

/// <summary>Cofre no painel (só o Dono): segredos (metadados) e o certificado A1.</summary>
public interface IVaultPanel
{
    Task<IReadOnlyList<SecretMetadata>> SecretsAsync(CancellationToken ct = default);

    Task<CertificateView?> CertificateAsync(CancellationToken ct = default);

    /// <summary>Confere senha, validade e CNPJ antes de guardar; substitui o anterior.</summary>
    /// <exception cref="ArgumentException">Arquivo, senha ou certificado recusados (mensagem ao usuário, sem a senha).</exception>
    Task<CertificateView> SaveCertificateAsync(byte[] pfx, string password, CancellationToken ct = default);

    Task<bool> RemoveCertificateAsync(CancellationToken ct = default);

    Task<CertificateAlert?> AlertAsync(CancellationToken ct = default);
}

public static class CertificateAlerts
{
    public const int NoticeDays = 30;
    public const int WarningDays = 15;
    public const int UrgentDays = 5;

    /// <summary>Dias inteiros completos até o vencimento (negativo depois de vencer).</summary>
    public static int DaysLeft(DateTimeOffset expiresAt, DateTimeOffset now) => (int)Math.Floor((expiresAt - now).TotalDays);

    public static CertificateAlert? For(DateTimeOffset expiresAt, DateTimeOffset now)
    {
        var days = DaysLeft(expiresAt, now);
        CertificateAlertLevel? level = expiresAt <= now ? CertificateAlertLevel.Expired
            : days < UrgentDays + 1 ? CertificateAlertLevel.Urgent
            : days < WarningDays + 1 ? CertificateAlertLevel.Warning
            : days < NoticeDays + 1 ? CertificateAlertLevel.Notice
            : null;
        return level is { } l ? new CertificateAlert(expiresAt, days, l) : null;
    }

    /// <summary>
    /// Dias em que o Worker registra o aviso no log (RF06 CA5: 30, 15 e 5 dias antes; no último dia, 0; e no primeiro dia
    /// depois de vencer, −1). Uma conferência por dia: cada aviso sai uma vez.
    /// </summary>
    public static bool IsReminderDay(int daysLeft) => daysLeft is NoticeDays or WarningDays or UrgentDays or 0 or -1;
}
