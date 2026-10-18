using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Channels;

public enum ChannelImportStatus
{
    /// <summary>Lendo a lista de anúncios do vendedor no canal, página por página.</summary>
    Listing,
    /// <summary>Lista completa: os anúncios estão sendo importados em lotes pela fila.</summary>
    Processing,
    Completed,
    /// <summary>Parou antes do fim (ex.: o canal pediu nova autorização). O que já entrou fica.</summary>
    Failed,
}

/// <summary>Resultado de um anúncio na importação.</summary>
public enum ChannelImportItemOutcome
{
    /// <summary>Virou peça nova, já vinculada ao anúncio.</summary>
    Created,
    /// <summary>Ligado a uma peça que já existia com o mesmo código (a peça não muda; vale o estoque da loja).</summary>
    Linked,
    /// <summary>O anúncio já estava vinculado (reimportação): nada muda.</summary>
    AlreadyLinked,
    /// <summary>Não importado; o motivo vai para o relatório.</summary>
    Rejected,
}

/// <summary>Anúncio que não entrou como esperado, com o motivo para o relatório.</summary>
public sealed record ChannelImportIssue(string ExternalId, string Message);

/// <summary>
/// Importação dos anúncios que a loja já tem num canal (RF11 CA2, CA3). Primeiro lista os ids (páginas encadeadas pela
/// fila), depois importa em lotes, com o progresso salvo junto de cada lote: se o lote cair, ele é repetido do mesmo ponto.
/// </summary>
public sealed class ChannelImport : ITenantOwned
{
    public const int MaxItems = 5000;
    public const int MaxListedIssues = 500;

    private ChannelImport() { }

    public ChannelImport(Guid tenantId, Guid connectionId, Guid userId, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty || connectionId == Guid.Empty) throw new ArgumentException("Loja e conexão obrigatórias.", nameof(connectionId));
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        ConnectionId = connectionId;
        UserId = userId;
        Status = ChannelImportStatus.Listing;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ConnectionId { get; private set; }

    /// <summary>Quem pediu: as peças nascem com o movimento de estoque em nome dele.</summary>
    public Guid UserId { get; private set; }

    public ChannelImportStatus Status { get; private set; }

    /// <summary>Ids dos anúncios no canal, na ordem em que vieram; importados a partir de <see cref="ProcessedItems"/>.</summary>
    public List<string> ExternalIds { get; private set; } = [];

    /// <summary>Cursor da próxima página da lista (nulo na primeira).</summary>
    public string? Cursor { get; private set; }

    /// <summary>A conta tinha mais anúncios que o limite: os excedentes ficaram de fora.</summary>
    public bool Truncated { get; private set; }

    public int ProcessedItems { get; private set; }
    public int CreatedCount { get; private set; }
    public int LinkedCount { get; private set; }
    public int AlreadyLinkedCount { get; private set; }
    public int RejectedCount { get; private set; }
    public List<ChannelImportIssue> Issues { get; private set; } = [];
    public string? FailureReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }

    public int TotalItems => ExternalIds.Count;

    public bool IsRunning => Status is ChannelImportStatus.Listing or ChannelImportStatus.Processing;

    /// <summary>Uma página da lista. Sem próxima página (ou no limite), passa a importar; lista vazia termina na hora.</summary>
    public void AddPage(IEnumerable<string> ids, string? nextCursor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (Status != ChannelImportStatus.Listing) throw new InvalidOperationException("A lista de anúncios já foi lida.");
        var known = ExternalIds.ToHashSet(StringComparer.Ordinal);
        var page = ids.Select(i => i.Trim()).Where(i => i.Length is > 0 and <= ChannelListing.ExternalIdMaxLength).ToList();
        foreach (var id in page)
        {
            if (ExternalIds.Count >= MaxItems)
            {
                Truncated = true;
                break;
            }
            if (known.Add(id)) ExternalIds.Add(id);
        }
        Cursor = nextCursor;
        if (page.Count == 0 || string.IsNullOrEmpty(nextCursor) || Truncated)
        {
            Cursor = null;
            Status = ChannelImportStatus.Processing;
            if (ExternalIds.Count == 0) Finish(now);
        }
    }

    /// <summary>Próximo lote a importar (vazio se não está importando ou já acabou).</summary>
    public IReadOnlyList<string> NextBatch(int size) =>
        Status == ChannelImportStatus.Processing ? ExternalIds.Skip(ProcessedItems).Take(size).ToList() : [];

    /// <summary>Resultado de um lote, gravado na mesma transação das peças.</summary>
    public void RecordBatch(IReadOnlyList<(ChannelImportItemOutcome Outcome, ChannelImportIssue? Issue)> results, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (Status != ChannelImportStatus.Processing) throw new InvalidOperationException("A importação não está em andamento.");
        if (results.Count == 0 || ProcessedItems + results.Count > ExternalIds.Count) throw new ArgumentException("Lote inconsistente.", nameof(results));
        foreach (var (outcome, issue) in results)
        {
            switch (outcome)
            {
                case ChannelImportItemOutcome.Created: CreatedCount++; break;
                case ChannelImportItemOutcome.Linked: LinkedCount++; break;
                case ChannelImportItemOutcome.AlreadyLinked: AlreadyLinkedCount++; break;
                default: RejectedCount++; break;
            }
            if (issue is not null && Issues.Count < MaxListedIssues) Issues.Add(issue);
        }
        ProcessedItems += results.Count;
        if (ProcessedItems == ExternalIds.Count) Finish(now);
    }

    public void Fail(string reason, DateTimeOffset now)
    {
        if (!IsRunning) return;
        FailureReason = reason.Length > 300 ? reason[..300] : reason;
        Status = ChannelImportStatus.Failed;
        FinishedAt = now;
    }

    private void Finish(DateTimeOffset now)
    {
        Status = ChannelImportStatus.Completed;
        FinishedAt = now;
    }
}
