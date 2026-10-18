using System.Globalization;
using System.Text.RegularExpressions;
using Ecommerce.Application.Email;

namespace Ecommerce.Application.Reports;

/// <summary>
/// Pedido do agendador (Worker) para a loja do escopo: confere a sincronização e, havendo problema novo, avisa os Donos
/// por e-mail (RF33). Os Donos vêm do banco da plataforma no próprio pedido.
/// </summary>
public sealed record CheckSyncHealth(IReadOnlyList<DailySummaryRecipient> Owners);

/// <summary>Um e-mail de alerta, pela fila (falha do servidor de e-mail é tentada de novo — RF28).</summary>
public sealed record SendSyncAlertEmail(EmailMessage Message);

public enum SyncAlertOutcome
{
    Sent,
    /// <summary>Desligado, sem Dono com e-mail, sem problema ou só problemas já avisados nas últimas 24 horas.</summary>
    Skipped,
}

public enum SyncProblemKind
{
    /// <summary>Processamento em segundo plano que esgotou as tentativas (RF28) — está em Integrações, para reprocessar.</summary>
    BackgroundFailure,
    /// <summary>A mesma chamada a um serviço externo falhou várias vezes seguidas e ainda não voltou a dar certo (RF29).</summary>
    RepeatedCallFailure,
    /// <summary>Conexão de canal que perdeu a autorização (RF21 CA2).</summary>
    ChannelReauth,
}

public enum SyncAction
{
    Reconnect,
    FixPart,
    Replay,
    WaitService,
    ContactSupport,
}

/// <summary>Uma chamada externa recente, como sai do log de integração (RF29).</summary>
public sealed record IntegrationCall(string Channel, string Operation, bool Success, DateTimeOffset At, string? Error, bool AboutPart);

/// <summary>
/// Problema encontrado pelas consultas (RF33 CA1). <paramref name="Key"/> identifica o problema entre rodadas (para não
/// avisar de novo em 24 horas); <paramref name="LastError"/> já vem redigido do log.
/// </summary>
public sealed record SyncProblem(
    string Key, SyncProblemKind Kind, string? Channel, string What, int Occurrences, DateTimeOffset LastAt, string? LastError, bool AboutPart);

/// <summary>Problema com o diagnóstico em linguagem simples e a ação sugerida (fixa, das regras).</summary>
public sealed record SyncDiagnosis(
    string Key, SyncProblemKind Kind, string Title, int Occurrences, DateTimeOffset LastAt, string? LastError, string Explanation,
    SyncAction Action, string ActionText);

public sealed record SyncGuardianSettingsView(bool Enabled);

/// <summary>Guardião no painel: os problemas de agora (sem IA) e o aviso por e-mail.</summary>
public interface ISyncGuardian
{
    Task<IReadOnlyList<SyncDiagnosis>> CurrentAsync(CancellationToken ct = default);

    Task<SyncGuardianSettingsView> SettingsAsync(CancellationToken ct = default);

    Task<SyncGuardianSettingsView> SaveAsync(bool enabled, CancellationToken ct = default);
}

/// <summary>
/// Regras fixas do guardião (RF33): o que é problema, que ação sugerir e a conferência do texto da IA. A IA só explica o
/// erro em linguagem simples; a ação e os números vêm sempre daqui.
/// </summary>
public static partial class SyncGuardianRules
{
    /// <summary>Janela das chamadas que falharam.</summary>
    public static readonly TimeSpan CallWindow = TimeSpan.FromHours(2);

    /// <summary>Falhas em segundo plano consideradas (as mais antigas já foram avisadas e seguem em Integrações).</summary>
    public static readonly TimeSpan FailureWindow = TimeSpan.FromHours(24);

    /// <summary>O mesmo problema só é avisado de novo depois disto, se continuar.</summary>
    public static readonly TimeSpan RepeatAfter = TimeSpan.FromHours(24);

    /// <summary>Falhas seguidas da mesma chamada para virar problema (uma falha isolada é tentada de novo sozinha).</summary>
    public const int MinCallFailures = 3;

    public const int MaxExplanationLength = 300;

    public static string ChannelLabel(string? channel) => channel switch
    {
        "mercado-livre" => "Mercado Livre",
        "meta" => "Instagram/Facebook",
        "mercado-pago" => "Mercado Pago",
        "cielo" => "Cielo",
        "melhor-envio" => "Melhor Envio",
        "focus-nfe" => "Focus NFe",
        "fake" => "Serviço simulado",
        null or "" => "Serviço externo",
        _ => channel,
    };

    /// <summary>Processamento que mexe em anúncio ou item de catálogo: um erro de dados costuma estar na peça.</summary>
    public static bool IsListingWork(string description) =>
        description.Contains("anúncio", StringComparison.OrdinalIgnoreCase) || description.Contains("catálogo", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Chamadas que falharam pelo menos <see cref="MinCallFailures"/> vezes na janela e cuja última tentativa ainda falhou
    /// (se voltou a dar certo, não é problema). Uma por canal e operação.
    /// </summary>
    public static IReadOnlyList<SyncProblem> RepeatedCallFailures(IEnumerable<IntegrationCall> calls, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(calls);
        var since = now - CallWindow;
        return calls.Where(c => c.At >= since)
            .GroupBy(c => (c.Channel, c.Operation))
            .Select(g =>
            {
                var ordered = g.OrderBy(c => c.At).ToList();
                var lastSuccess = ordered.LastOrDefault(c => c.Success)?.At;
                var failures = ordered.Where(c => !c.Success && (lastSuccess is null || c.At > lastSuccess)).ToList();
                return (g.Key, failures);
            })
            .Where(x => x.failures.Count >= MinCallFailures)
            .Select(x => new SyncProblem($"chamada:{x.Key.Channel}:{x.Key.Operation}", SyncProblemKind.RepeatedCallFailure, x.Key.Channel, x.Key.Operation,
                x.failures.Count, x.failures[^1].At, x.failures.LastOrDefault(f => f.Error is not null)?.Error, x.failures.Any(f => f.AboutPart)))
            .OrderByDescending(p => p.LastAt)
            .ToList();
    }

    /// <summary>Os problemas que ainda não foram avisados nas últimas 24 horas.</summary>
    public static IReadOnlyList<SyncProblem> ToAlert(IEnumerable<SyncProblem> problems, IEnumerable<string> alertedKeys)
    {
        ArgumentNullException.ThrowIfNull(problems);
        var alerted = alertedKeys.ToHashSet(StringComparer.Ordinal);
        return problems.Where(p => !alerted.Contains(p.Key)).ToList();
    }

    public static SyncAction Classify(SyncProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var error = problem.LastError ?? string.Empty;
        if (problem.Kind == SyncProblemKind.ChannelReauth || AuthError().IsMatch(error)) return SyncAction.Reconnect;
        if (problem.AboutPart && DataError().IsMatch(error)) return SyncAction.FixPart;
        if (problem.Kind == SyncProblemKind.BackgroundFailure) return SyncAction.Replay;
        return ServiceDown().IsMatch(error) ? SyncAction.WaitService : SyncAction.ContactSupport;
    }

    public static string ActionText(SyncAction action) => action switch
    {
        SyncAction.Reconnect => "Abra Canais de venda e conecte a conta de novo.",
        SyncAction.FixPart => "Abra a peça, corrija o dado apontado no erro e, se a falha estiver em Integrações, clique em Reprocessar.",
        SyncAction.Replay => "Quando o serviço voltar, clique em Reprocessar em Integrações.",
        SyncAction.WaitService => "O serviço parece fora do ar e o sistema tenta de novo sozinho. Se passar de algumas horas, fale com o suporte.",
        _ => "Se continuar, fale com o suporte da plataforma e informe o horário da falha.",
    };

    public static string Title(SyncProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return problem.Kind switch
        {
            SyncProblemKind.ChannelReauth => $"{ChannelLabel(problem.Channel)}: a conexão perdeu a autorização",
            SyncProblemKind.RepeatedCallFailure => $"{ChannelLabel(problem.Channel)}: “{problem.What}” está falhando",
            _ => $"Falhou: {problem.What}",
        };
    }

    /// <summary>Explicação fixa, usada quando a IA está fora do ar ou o texto dela é recusado.</summary>
    public static string FallbackExplanation(SyncProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var n = problem.Occurrences.ToString(CultureInfo.InvariantCulture);
        return problem.Kind switch
        {
            SyncProblemKind.ChannelReauth => $"A loja precisa autorizar de novo o acesso a {ChannelLabel(problem.Channel)}. Enquanto isso, nada sincroniza com esse canal.",
            SyncProblemKind.RepeatedCallFailure => problem.Occurrences == 1 ? "A chamada falhou." : $"A chamada falhou {n} vezes seguidas nas últimas 2 horas.",
            _ => problem.Occurrences == 1
                ? "O processamento falhou depois de todas as tentativas automáticas."
                : $"O processamento falhou {n} vezes depois de todas as tentativas automáticas.",
        };
    }

    /// <summary>
    /// RF33 CA1: a explicação da IA só entra se for curta, sem link nem e-mail, e se todo número citado estiver no próprio
    /// problema (quantidade ou o código do erro). Recusada, vale a explicação fixa.
    /// </summary>
    public static string? AcceptExplanation(string? text, SyncProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxExplanationLength) return null;
        if (trimmed.Contains("http", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("www.", StringComparison.OrdinalIgnoreCase) || trimmed.Contains('@'))
            return null;
        var allowed = new HashSet<string>(StringComparer.Ordinal) { problem.Occurrences.ToString(CultureInfo.InvariantCulture), "2", "24" };
        foreach (Match m in Number().Matches(problem.LastError ?? string.Empty)) allowed.Add(m.Value);
        foreach (Match m in Number().Matches(trimmed))
            if (!allowed.Contains(m.Value)) return null;
        return trimmed;
    }

    public static SyncDiagnosis Diagnose(SyncProblem problem, string? aiExplanation = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var action = Classify(problem);
        return new SyncDiagnosis(problem.Key, problem.Kind, Title(problem), problem.Occurrences, problem.LastAt, problem.LastError,
            AcceptExplanation(aiExplanation, problem) ?? FallbackExplanation(problem), action, ActionText(action));
    }

    [GeneratedRegex(@"\b(401|403)\b|unauthori[sz]ed|forbidden|invalid_(token|grant)|token (expirado|inválido|vencido)|autoriza", RegexOptions.IgnoreCase)]
    private static partial Regex AuthError();

    [GeneratedRegex(@"\b(400|409|422)\b|inv[aá]lid|obrigat[oó]ri|required|missing|not allowed", RegexOptions.IgnoreCase)]
    private static partial Regex DataError();

    [GeneratedRegex(@"\b5\d\d\b|timeout|timed out|tempo esgotado|indispon[ií]vel|unavailable|connection|conex[aã]o", RegexOptions.IgnoreCase)]
    private static partial Regex ServiceDown();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Number();
}
