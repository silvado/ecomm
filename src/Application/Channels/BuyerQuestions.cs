using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ecommerce.Domain.Channels;

namespace Ecommerce.Application.Channels;

/// <summary>
/// Pergunta como o canal a mostra agora (consultada na API depois do aviso — o aviso nunca é confiado). No ML,
/// <see cref="Status"/> é <c>UNANSWERED</c>, <c>ANSWERED</c>, <c>BANNED</c>, <c>CLOSED_UNANSWERED</c>, <c>DELETED</c>,
/// <c>DISABLED</c> ou <c>UNDER_REVIEW</c>. Sem dados do comprador (LGPD): só o texto.
/// </summary>
public sealed record ChannelQuestionSnapshot(
    string Id, string ItemId, string Status, string Text, DateTimeOffset? AskedAt, string? AnswerText, DateTimeOffset? AnsweredAt);

/// <summary>Consulta a pergunta no canal e a registra (ou atualiza) na loja do escopo. Uma por aviso de pergunta.</summary>
public sealed record ImportBuyerQuestion(Guid ConnectionId, string QuestionId);

/// <summary>Gera (ou refaz) o rascunho da IA para a pergunta (RF31). <paramref name="UserId"/>: quem pediu de novo no painel.</summary>
public sealed record DraftBuyerAnswer(Guid QuestionId, Guid? UserId = null);

public enum DraftOutcome
{
    Drafted,
    /// <summary>Simples e com a resposta automática ligada: a resposta foi mandada para envio.</summary>
    SentAutomatically,
    /// <summary>A IA não ajudou (fora do ar, resposta inútil, anúncio sem peça): fica a nota para a loja responder à mão.</summary>
    NoDraft,
    /// <summary>A pergunta já foi respondida ou encerrada.</summary>
    Skipped,
}

/// <summary>Envia a resposta ao canal. <paramref name="UserId"/> nulo = resposta automática (falha volta à fila — RF28).</summary>
public sealed record SendBuyerAnswer(Guid QuestionId, string Text, Guid? UserId);

public enum AnswerOutcome
{
    Sent,
    NotFound,
    /// <summary>Já respondida ou encerrada.</summary>
    Conflict,
    /// <summary>O canal recusou (pergunta excluída lá, texto não permitido): a mensagem do canal vai para a loja.</summary>
    Refused,
    Invalid,
    /// <summary>Canal fora do ar ou conexão sem autorização: tente de novo.</summary>
    Unavailable,
}

public sealed record AnswerResult(AnswerOutcome Outcome, string? Message = null);

/// <summary>Pergunta na lista do painel (RF31).</summary>
public sealed record BuyerQuestionView(
    Guid Id, string Channel, string ExternalItemId, Guid? PartId, string? PartTitle, string Text, DateTimeOffset AskedAt, BuyerQuestionStatus Status,
    string? DraftText, QuestionKind? Kind, string? DraftNote, string? AnswerText, AnswerOrigin? AnsweredVia, DateTimeOffset? AnsweredAt, string? LastError);

public sealed record QuestionSettingsView(bool AutoAnswer);

/// <summary>Perguntas de compradores da loja do escopo (RF31): lista e opção de resposta automática.</summary>
public interface IBuyerQuestions
{
    /// <param name="pendingOnly">Só as que esperam resposta (padrão do painel).</param>
    Task<IReadOnlyList<BuyerQuestionView>> ListAsync(bool pendingOnly, CancellationToken ct = default);

    Task<BuyerQuestionView?> GetAsync(Guid id, CancellationToken ct = default);

    Task<QuestionSettingsView> SettingsAsync(CancellationToken ct = default);

    Task<QuestionSettingsView> SetAutoAnswerAsync(bool enabled, CancellationToken ct = default);
}

/// <summary>O que a IA devolveu para a pergunta (formato pedido em <c>BuyerAnswerAdvisor</c>).</summary>
public sealed record AiBuyerAnswer(bool Simple, bool AboutCompatibility, string Answer, string Reason);

/// <summary>Decisão final: tipo, rascunho, nota para a loja e se sai sozinha.</summary>
public sealed record AnswerDecision(QuestionKind Kind, string? Draft, string? Note, bool SendNow);

/// <summary>
/// Regras fixas por cima da IA (RF31 CA2/CA3, RF35): a IA classifica, mas só sai sozinha a resposta que passa por todas.
/// Compatibilidade nunca sai sozinha — HIPÓTESE conservadora para CA3: mesmo com o veículo no cadastro, a loja confere —, nem
/// resposta com contato (proibido no ML), preço ou desconto (a IA não mexe em preço). Conferidas pelo conjunto de avaliação
/// (tests/Application.Tests/Channels/buyer-answer-eval.json), que inclui respostas "erradas" de propósito da IA.
/// </summary>
public static partial class BuyerAnswerRules
{
    /// <param name="knownCodes">Códigos da peça (OEM, código interno): podem aparecer na resposta sem parecer telefone.</param>
    public static AnswerDecision Decide(string question, AiBuyerAnswer ai, bool partHasCompatibilities, bool autoAnswer, IReadOnlyCollection<string> knownCodes)
    {
        ArgumentNullException.ThrowIfNull(ai);
        ArgumentNullException.ThrowIfNull(knownCodes);
        var answer = ai.Answer.Trim();
        if (answer.Length == 0)
            return new AnswerDecision(QuestionKind.Complex, null, "A IA não sugeriu resposta: responda à mão com os dados da peça.", false);

        var notes = new List<string>();
        if (!ai.Simple)
            notes.Add(ai.Reason.Trim() is { Length: > 0 } reason ? $"A IA pede revisão: {reason.TrimEnd('.')}." : "A IA pede revisão: falta informação no cadastro.");
        if (ai.AboutCompatibility || AsksAboutVehicle(question))
            notes.Add(partHasCompatibilities
                ? "Pergunta sobre compatibilidade: confira os veículos da peça antes de enviar."
                : "Pergunta sobre compatibilidade e a peça não tem veículos cadastrados: não confirme sem conferir.");
        if (HasContactData(answer, knownCodes))
            notes.Add("A sugestão tem telefone, e-mail ou site, o que o Mercado Livre não permite: revise.");
        if (MentionsMoney(answer))
            notes.Add("A sugestão fala de preço, desconto ou pagamento: revise.");
        if (answer.Length > BuyerQuestion.AnswerMaxLength)
        {
            notes.Add($"A sugestão passou de {BuyerQuestion.AnswerMaxLength} caracteres: encurte.");
            answer = answer[..BuyerQuestion.AnswerMaxLength];
        }

        var simple = notes.Count == 0;
        return new AnswerDecision(simple ? QuestionKind.Simple : QuestionKind.Complex, answer,
            notes.Count == 0 ? null : string.Join(' ', notes), simple && autoAnswer);
    }

    private static readonly string[] VehicleWords =
    [
        "serve", "servir", "serviria", "compativel", "compatibilidade", "encaixa", "encaixe", "funciona", "cabe", "bate", "adapta",
        "adaptar", "meu carro", "minha moto", "ano", "modelo", "versao", "motor", "chassi",
    ];

    /// <summary>Pergunta que fala de veículo (servir, encaixar, ano, modelo...). Na dúvida, sim: vira rascunho.</summary>
    public static bool AsksAboutVehicle(string question)
    {
        var text = Plain(question);
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return VehicleWords.Any(w => w.Contains(' ', StringComparison.Ordinal) ? text.Contains(w, StringComparison.Ordinal) : words.Contains(w))
            || Year().IsMatch(text);
    }

    /// <summary>E-mail, site ou sequência de 8+ dígitos (telefone) que não é um código da peça.</summary>
    public static bool HasContactData(string answer, IReadOnlyCollection<string> knownCodes)
    {
        ArgumentNullException.ThrowIfNull(knownCodes);
        if (Email().IsMatch(answer) || Site().IsMatch(answer)) return true;
        var text = answer;
        foreach (var code in knownCodes.Where(c => c.Length > 0).OrderByDescending(c => c.Length))
            text = text.Replace(code, " ", StringComparison.OrdinalIgnoreCase);
        var codes = knownCodes.Select(DigitsOnly).Where(c => c.Length >= 8).ToHashSet(StringComparer.Ordinal);
        return Phone().Matches(text).Any(m => !codes.Contains(DigitsOnly(m.Value)));
    }

    public static bool MentionsMoney(string answer) =>
        Money().IsMatch(answer) || Plain(answer).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(w => w is "desconto" or "descontos" or "pix" or "boleto" or "parcelo" or "parcelado" or "parcelas" or "barato" or "negocio" or "negociar");

    /// <summary>Minúsculas, sem acento, só letras, dígitos e espaço.</summary>
    public static string Plain(string text)
    {
        var plain = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            plain.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return string.Join(' ', plain.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string DigitsOnly(string text) => new(text.Where(char.IsAsciiDigit).ToArray());

    [GeneratedRegex(@"\b(19[5-9]\d|20[0-4]\d)\b")]
    private static partial Regex Year();

    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.]+")]
    private static partial Regex Email();

    [GeneratedRegex(@"(https?://|www\.|\.com(\.br)?\b|\.net\b|\.org\b|wa\.me|instagram|whats\s?app|zap\b|facebook)", RegexOptions.IgnoreCase)]
    private static partial Regex Site();

    [GeneratedRegex(@"\d(?:[\s().-]*\d){7,}")]
    private static partial Regex Phone();

    [GeneratedRegex(@"R\$|\breais\b|\d+\s*%", RegexOptions.IgnoreCase)]
    private static partial Regex Money();
}
