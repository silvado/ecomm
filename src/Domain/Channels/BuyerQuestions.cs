using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Channels;

public enum BuyerQuestionStatus
{
    /// <summary>Sem resposta: aparece no painel para a loja responder (com rascunho da IA, quando houver).</summary>
    Pending,
    Answered,
    /// <summary>Não aceita mais resposta: excluída, banida ou encerrada no canal.</summary>
    Closed,
}

/// <summary>RF31 CA2: pergunta que a IA responde só com o cadastro (simples) ou que pede a loja (complexa).</summary>
public enum QuestionKind
{
    Simple,
    Complex,
}

public enum AnswerOrigin
{
    /// <summary>Enviada pelo painel (rascunho da IA revisado ou texto da loja).</summary>
    Panel,
    /// <summary>Resposta automática de pergunta simples (só com a opção ligada — RF31 CA2/CA4).</summary>
    Automatic,
    /// <summary>Respondida direto no canal (ex.: no site do Mercado Livre).</summary>
    Channel,
}

/// <summary>
/// Pergunta de comprador num anúncio do canal (RF31; hoje só o Mercado Livre). O rascunho da IA é só sugestão: só vira
/// resposta enviada pela loja ou, com a resposta automática ligada, quando a pergunta é simples (RF35).
/// </summary>
public sealed class BuyerQuestion : ITenantOwned
{
    public const int ExternalIdMaxLength = 30;
    public const int TextMaxLength = 2_000;

    /// <summary>HIPÓTESE: limite da resposta (a documentação do ML não informa); 2.000 caracteres, como a pergunta.</summary>
    public const int AnswerMaxLength = 2_000;

    public const int NoteMaxLength = 300;

    private BuyerQuestion() { }

    public BuyerQuestion(Guid tenantId, Guid connectionId, string externalId, string externalItemId, Guid? partId, string text, DateTimeOffset askedAt,
        DateTimeOffset now)
    {
        if (tenantId == Guid.Empty || connectionId == Guid.Empty) throw new ArgumentException("Loja e conexão obrigatórias.", nameof(connectionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalItemId);
        if (externalId.Length > ExternalIdMaxLength || externalItemId.Length > ChannelListing.ExternalIdMaxLength)
            throw new ArgumentException("Id da pergunta ou do anúncio grande demais.", nameof(externalId));
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        ConnectionId = connectionId;
        ExternalId = externalId.Trim();
        ExternalItemId = externalItemId.Trim();
        PartId = partId;
        Text = Cut(text.Trim(), TextMaxLength)!;
        AskedAt = askedAt;
        CreatedAt = now;
        Status = BuyerQuestionStatus.Pending;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ConnectionId { get; private set; }

    /// <summary>Id da pergunta no canal.</summary>
    public string ExternalId { get; private set; } = string.Empty;

    /// <summary>Anúncio perguntado (id no canal).</summary>
    public string ExternalItemId { get; private set; } = string.Empty;

    /// <summary>Peça do anúncio; nulo se o anúncio não é da plataforma (a loja responde à mão, sem IA).</summary>
    public Guid? PartId { get; private set; }

    public string Text { get; private set; } = string.Empty;
    public DateTimeOffset AskedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public BuyerQuestionStatus Status { get; private set; }

    public string? DraftText { get; private set; }
    public QuestionKind? Kind { get; private set; }

    /// <summary>Por que o rascunho precisa de revisão (ou por que não há rascunho), em linguagem da loja.</summary>
    public string? DraftNote { get; private set; }

    public DateTimeOffset? DraftedAt { get; private set; }

    public string? AnswerText { get; private set; }
    public AnswerOrigin? AnsweredVia { get; private set; }

    /// <summary>Quem enviou pelo painel (nulo na automática e na respondida no canal).</summary>
    public Guid? AnsweredBy { get; private set; }

    public DateTimeOffset? AnsweredAt { get; private set; }

    /// <summary>Última falha ao enviar a resposta (mensagem para a loja).</summary>
    public string? LastError { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    /// <summary>Rascunho da IA (ou só a nota, se a IA não pôde ajudar). Só vale para pergunta ainda sem resposta.</summary>
    public void Drafted(string? text, QuestionKind? kind, string? note, DateTimeOffset now)
    {
        if (Status != BuyerQuestionStatus.Pending) return;
        DraftText = Cut(text?.Trim(), AnswerMaxLength);
        Kind = kind;
        DraftNote = Cut(note?.Trim(), NoteMaxLength);
        DraftedAt = now;
        UpdatedAt = now;
    }

    public void Answered(string text, AnswerOrigin via, Guid? userId, DateTimeOffset at, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (!Enum.IsDefined(via)) throw new ArgumentOutOfRangeException(nameof(via));
        Status = BuyerQuestionStatus.Answered;
        AnswerText = Cut(text.Trim(), AnswerMaxLength);
        AnsweredVia = via;
        AnsweredBy = via == AnswerOrigin.Panel ? userId : null;
        AnsweredAt = at;
        LastError = null;
        UpdatedAt = now;
    }

    public void SendFailed(string error, DateTimeOffset now)
    {
        LastError = Cut(error, ChannelListing.ErrorMaxLength);
        UpdatedAt = now;
    }

    /// <summary>Excluída, banida ou encerrada no canal: sai da lista de pendentes.</summary>
    public void Close(DateTimeOffset now)
    {
        if (Status == BuyerQuestionStatus.Answered) return;
        Status = BuyerQuestionStatus.Closed;
        UpdatedAt = now;
    }

    /// <summary>Resposta válida para enviar: sem espaços nas pontas, não vazia e no limite.</summary>
    public static string NormalizeAnswer(string? text)
    {
        var answer = text?.Replace("\r\n", "\n", StringComparison.Ordinal).Trim() ?? string.Empty;
        if (answer.Length == 0) throw new ArgumentException("Escreva a resposta.", nameof(text));
        if (answer.Length > AnswerMaxLength) throw new ArgumentException($"A resposta pode ter no máximo {AnswerMaxLength} caracteres.", nameof(text));
        return answer;
    }

    private static string? Cut(string? text, int max) => text is { Length: > 0 } ? (text.Length > max ? text[..max] : text) : null;
}
