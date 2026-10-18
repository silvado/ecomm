using Ecommerce.Application.Email;
using Ecommerce.Application.Reports;

namespace Ecommerce.Application.Vault;

/// <summary>
/// Pedido do monitor do certificado (Worker) para a loja do escopo: avisa os Donos por e-mail num dia de aviso (RF06 CA5:
/// 30, 15 e 5 dias antes, no dia em que vence e no dia seguinte). Os Donos vêm do banco da plataforma no próprio pedido.
/// </summary>
public sealed record SendCertificateReminder(DateTimeOffset ExpiresAt, int DaysLeft, IReadOnlyList<DailySummaryRecipient> Owners);

/// <summary>Um e-mail do aviso, pela fila (falha do servidor de e-mail é tentada de novo — RF28).</summary>
public sealed record SendCertificateReminderEmail(EmailMessage Message);

public enum CertificateReminderOutcome
{
    Sent,
    /// <summary>Já avisado neste dia para este certificado, sem Dono com e-mail ou o certificado mudou.</summary>
    Skipped,
}

/// <summary>Textos fixos do aviso (sem IA).</summary>
public static class CertificateReminderTexts
{
    public static string Subject(int daysLeft, string storeName) => daysLeft switch
    {
        < 0 => $"Certificado digital vencido — {storeName}",
        0 => $"Certificado digital vence hoje — {storeName}",
        1 => $"Certificado digital vence amanhã — {storeName}",
        _ => $"Certificado digital vence em {daysLeft} dias — {storeName}",
    };

    public static string Headline(int daysLeft) => daysLeft switch
    {
        < 0 => "O certificado digital A1 da loja venceu. A emissão de NF-e pela plataforma está parada até você enviar um certificado novo.",
        0 => "O certificado digital A1 da loja vence hoje. Depois disso, a emissão de NF-e pela plataforma para.",
        1 => "O certificado digital A1 da loja vence amanhã. Depois disso, a emissão de NF-e pela plataforma para.",
        _ => $"O certificado digital A1 da loja vence em {daysLeft} dias. Depois disso, a emissão de NF-e pela plataforma para.",
    };

    public const string Action =
        "Renove o certificado com a sua certificadora (ou com o contador) e envie o arquivo novo (.pfx) com a senha em Cofre → Certificado digital.";
}
