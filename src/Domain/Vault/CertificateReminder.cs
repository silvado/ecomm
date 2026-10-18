using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Vault;

/// <summary>
/// Aviso de vencimento do certificado A1 já enviado por e-mail aos Donos (RF06 CA5). Um por certificado (validade) e dia
/// de aviso: o índice único impede o e-mail em dobro quando o Worker reinicia no mesmo dia; um certificado novo (outra
/// validade) recomeça os avisos.
/// </summary>
public sealed class CertificateReminder : ITenantOwned
{
    private CertificateReminder() { }

    public CertificateReminder(Guid tenantId, DateTimeOffset certificateExpiresAt, int daysLeft, int recipients, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Loja obrigatória.", nameof(tenantId));
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        CertificateExpiresAt = certificateExpiresAt;
        DaysLeft = daysLeft;
        Recipients = Math.Max(0, recipients);
        SentAt = now;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>Validade do certificado avisado (identifica o certificado).</summary>
    public DateTimeOffset CertificateExpiresAt { get; private set; }

    /// <summary>Dia do aviso: 30, 15, 5, 0 (vence hoje) ou −1 (venceu).</summary>
    public int DaysLeft { get; private set; }

    public int Recipients { get; private set; }
    public DateTimeOffset SentAt { get; private set; }
}
