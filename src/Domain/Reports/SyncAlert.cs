using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Reports;

/// <summary>
/// Alerta do guardião da sincronização já enviado aos Donos (RF33). Guarda as chaves dos problemas avisados: o mesmo
/// problema só volta a ser avisado depois de 24 horas, enquanto continuar.
/// </summary>
public sealed class SyncAlert : ITenantOwned
{
    public const int MaxProblems = 20;
    public const int KeyMaxLength = 200;

    private SyncAlert() { }

    public SyncAlert(Guid tenantId, IEnumerable<string> problemKeys, int recipients, bool aiText, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Loja obrigatória.", nameof(tenantId));
        ArgumentNullException.ThrowIfNull(problemKeys);
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        ProblemKeys = problemKeys.Select(k => k.Length > KeyMaxLength ? k[..KeyMaxLength] : k).Distinct(StringComparer.Ordinal).Take(MaxProblems).ToList();
        if (ProblemKeys.Count == 0) throw new ArgumentException("Alerta sem problema.", nameof(problemKeys));
        Recipients = Math.Max(0, recipients);
        AiText = aiText;
        SentAt = now;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public DateTimeOffset SentAt { get; private set; }

    /// <summary>Chaves dos problemas avisados (ex.: "chamada:mercado-livre:atualizar anúncio").</summary>
    public List<string> ProblemKeys { get; private set; } = [];

    /// <summary>Quantos e-mails foram mandados para a fila.</summary>
    public int Recipients { get; private set; }

    /// <summary>Alguma explicação da IA passou na conferência e foi usada.</summary>
    public bool AiText { get; private set; }
}
