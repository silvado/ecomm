using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Reports;

/// <summary>
/// Resumo diário já enviado (RF34): um por loja e dia resumido. A linha é criada antes de montar o e-mail — a unicidade
/// de (loja, dia) impede o envio em dobro quando dois Workers ou duas rodadas se cruzam.
/// </summary>
public sealed class DailySummary : ITenantOwned
{
    private DailySummary() { }

    public DailySummary(Guid tenantId, DateOnly day, int recipients, bool aiText, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Loja obrigatória.", nameof(tenantId));
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        Day = day;
        Recipients = Math.Max(0, recipients);
        AiText = aiText;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>Dia resumido (ontem, no horário de Brasília).</summary>
    public DateOnly Day { get; private set; }

    /// <summary>Quantos e-mails foram mandados para a fila.</summary>
    public int Recipients { get; private set; }

    /// <summary>O texto de abertura da IA passou na conferência e foi usado.</summary>
    public bool AiText { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
