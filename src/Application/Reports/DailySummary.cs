using System.Globalization;
using System.Text.RegularExpressions;
using Ecommerce.Application.Email;
using Ecommerce.Domain.Orders;

namespace Ecommerce.Application.Reports;

public sealed record DailySummaryRecipient(string Email);

/// <summary>
/// Pedido do agendador (Worker) para a loja do escopo: monta e envia o resumo de ontem, se já passou do horário da loja e
/// ainda não saiu. Os Donos vêm do banco da plataforma no próprio pedido (o handler só usa o banco da loja).
/// </summary>
public sealed record PrepareDailySummary(IReadOnlyList<DailySummaryRecipient> Owners);

/// <summary>Um e-mail do resumo, pela fila (falha do servidor de e-mail é tentada de novo — RF28).</summary>
public sealed record SendDailySummaryEmail(EmailMessage Message);

public enum DailySummaryOutcome
{
    Sent,
    /// <summary>Desligado, antes do horário, já enviado hoje ou sem Dono com e-mail.</summary>
    Skipped,
}

public sealed record ChannelSales(OrderOrigin Origin, int Orders, decimal Total);

/// <summary>Peça ativa com saldo que não vende há mais de 90 dias (ou nunca vendeu desde o cadastro).</summary>
public sealed record StalePart(Guid Id, string InternalCode, string Title, int Available, int DaysWithoutSale);

/// <summary>
/// Números do resumo (RF34 CA2): saem só de consultas ao banco. A IA recebe isto pronto e apenas redige a abertura.
/// </summary>
public sealed record DailySummaryFacts(
    DateOnly Day, IReadOnlyList<ChannelSales> Sales, int StaleParts, IReadOnlyList<StalePart> StaleSample, int PendingQuestions,
    DateTimeOffset? OldestPendingQuestion)
{
    public const int StaleDays = 90;

    public int Orders => Sales.Sum(s => s.Orders);
    public decimal Total => Sales.Sum(s => s.Total);
}

public sealed record DailySummarySettingsView(bool Enabled, int Hour);

/// <summary>Configuração e prévia do resumo diário da loja do escopo (Dono).</summary>
public interface IDailySummaries
{
    Task<DailySummarySettingsView> SettingsAsync(CancellationToken ct = default);

    /// <exception cref="ArgumentOutOfRangeException">Hora fora de 0–23.</exception>
    Task<DailySummarySettingsView> SaveAsync(bool enabled, int hour, CancellationToken ct = default);

    /// <summary>Os números de ontem, como iriam no e-mail (sem a IA).</summary>
    Task<DailySummaryFacts> PreviewAsync(CancellationToken ct = default);
}

/// <summary>Regras fixas do resumo (RF34): horário e conferência do texto da IA.</summary>
public static partial class DailySummaryRules
{
    public const int MaxAiTextLength = 600;

    /// <summary>Já é hora: a hora local passou da configurada (cobre o Worker parado no horário — sai assim que voltar, no mesmo dia).</summary>
    public static bool IsDue(DateTime localNow, int hour) => localNow.Hour >= hour;

    public static string OriginLabel(OrderOrigin origin) => origin switch
    {
        OrderOrigin.Site => "Loja virtual",
        OrderOrigin.Counter => "Balcão",
        OrderOrigin.MercadoLivre => "Mercado Livre",
        _ => origin.ToString(),
    };

    /// <summary>
    /// RF34 CA2: o texto da IA só entra se todo número citado nele existir nos fatos (contagens, valores, dias, a data).
    /// Número inventado ou conta feita pela IA descarta o texto — o e-mail sai só com os números das consultas.
    /// </summary>
    public static string? AcceptAiText(string? text, DailySummaryFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxAiTextLength) return null;
        var allowed = AllowedNumbers(facts);
        foreach (Match m in Number().Matches(trimmed))
            if (Parse(m.Value) is not { } n || !allowed.Contains(n)) return null;
        return trimmed;
    }

    private static HashSet<decimal> AllowedNumbers(DailySummaryFacts facts)
    {
        var allowed = new HashSet<decimal>
        {
            facts.Day.Day, facts.Day.Month, facts.Day.Year, facts.Orders, facts.Total, facts.StaleParts, facts.PendingQuestions, DailySummaryFacts.StaleDays,
        };
        foreach (var s in facts.Sales) { allowed.Add(s.Orders); allowed.Add(s.Total); }
        foreach (var p in facts.StaleSample) { allowed.Add(p.Available); allowed.Add(p.DaysWithoutSale); }
        return allowed;
    }

    /// <summary>"1.234,56" → 1234.56; "17" → 17; "10%" já vem sem o símbolo.</summary>
    private static decimal? Parse(string token)
    {
        var normalized = token.Contains(',', StringComparison.Ordinal)
            ? token.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.')
            : Thousands().IsMatch(token) ? token.Replace(".", string.Empty, StringComparison.Ordinal) : token;
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    [GeneratedRegex(@"\d+(?:[.,]\d+)*")]
    private static partial Regex Number();

    [GeneratedRegex(@"^\d{1,3}(\.\d{3})+$")]
    private static partial Regex Thousands();
}
