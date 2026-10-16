using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Ai;

/// <summary>Funcionalidades de IA (ADR-0003). Também são a chave do contador de uso (RF36).</summary>
public static class AiFeature
{
    public const string PartFromPhotos = "part_from_photos";
    public const string BuyerAnswer = "buyer_answer";
    public const string DailySummary = "daily_summary";
    public const string SyncDiagnosis = "sync_diagnosis";
    public const string PriceSuggestion = "price_suggestion";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { PartFromPhotos, BuyerAnswer, DailySummary, SyncDiagnosis, PriceSuggestion };

    /// <summary>
    /// Limite do plano que a funcionalidade consome (RF36). Decisão do usuário (16/10/2026): só o cadastro por foto tem
    /// limite; as outras são contadas e auditadas, sem limite, até os planos definirem.
    /// </summary>
    public static string? PlanLimitKey(string feature) => feature == PartFromPhotos ? Platform.PlanLimit.AiListingsPerMonth : null;
}

/// <summary>
/// Uma chamada de IA (RF35 CA1, regra 4 do CLAUDE.md): loja, usuário, funcionalidade, modelo, tokens, custo estimado,
/// entrada resumida (sem imagens nem dados pessoais) e saída. Gravada também quando falha.
/// </summary>
public sealed class AiInteraction : ITenantOwned
{
    public const int FeatureMaxLength = 40;
    public const int ModelMaxLength = 80;
    public const int SummaryMaxLength = 4_000;
    public const int OutputMaxLength = 20_000;
    public const int ErrorMaxLength = 500;

    private AiInteraction() { }

    public AiInteraction(Guid tenantId, Guid? userId, string feature, string model, bool success, int inputTokens, int outputTokens,
        decimal estimatedCostUsd, string inputSummary, string? output, string? error, int durationMs, string? correlationId, DateTimeOffset at)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Loja obrigatória.", nameof(tenantId));
        ArgumentException.ThrowIfNullOrWhiteSpace(feature);
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        UserId = userId;
        Feature = Cut(feature, FeatureMaxLength)!;
        Model = Cut(model, ModelMaxLength) ?? "desconhecido";
        Success = success;
        InputTokens = Math.Max(0, inputTokens);
        OutputTokens = Math.Max(0, outputTokens);
        EstimatedCostUsd = Math.Max(0, decimal.Round(estimatedCostUsd, 6));
        InputSummary = Cut(inputSummary, SummaryMaxLength) ?? string.Empty;
        Output = Cut(output, OutputMaxLength);
        Error = Cut(error, ErrorMaxLength);
        DurationMs = Math.Max(0, durationMs);
        CorrelationId = Cut(correlationId, 64);
        At = at;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>Quem pediu no painel; nulo em rotinas automáticas.</summary>
    public Guid? UserId { get; private set; }

    public string Feature { get; private set; } = string.Empty;
    public string Model { get; private set; } = string.Empty;
    public bool Success { get; private set; }
    public int InputTokens { get; private set; }
    public int OutputTokens { get; private set; }

    /// <summary>Custo estimado em dólares (tabela de preços por modelo na configuração).</summary>
    public decimal EstimatedCostUsd { get; private set; }

    public string InputSummary { get; private set; } = string.Empty;

    /// <summary>Resposta (JSON) como veio do modelo.</summary>
    public string? Output { get; private set; }

    public string? Error { get; private set; }
    public int DurationMs { get; private set; }
    public string? CorrelationId { get; private set; }
    public DateTimeOffset At { get; private set; }

    private static string? Cut(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length > max ? value[..max] : value;
}

/// <summary>Uso mensal de uma funcionalidade de IA pela loja (RF36 CA1). Só as chamadas bem-sucedidas ficam contadas.</summary>
public sealed class AiUsageCounter : ITenantOwned
{
    private AiUsageCounter() { }

    public Guid TenantId { get; private set; }
    public string Feature { get; private set; } = string.Empty;

    /// <summary>1º dia do mês (America/Sao_Paulo).</summary>
    public DateOnly Period { get; private set; }

    public int Used { get; private set; }

    public static DateOnly PeriodOf(DateOnly day) => new(day.Year, day.Month, 1);
}
