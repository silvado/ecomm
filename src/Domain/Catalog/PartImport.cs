using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Catalog;

public enum PartImportStatus
{
    /// <summary>Arquivo lido e validado; aguardando o usuário confirmar (prévia).</summary>
    Validated,
    /// <summary>Confirmada: as linhas válidas estão sendo cadastradas em lotes pela fila.</summary>
    Processing,
    Completed,
}

/// <summary>Linha válida da planilha, já normalizada, pronta para virar peça (RF11). Código vazio = gerado no cadastro.</summary>
public sealed record PartImportRow(
    int Line, string InternalCode, string Title, string? Description, PartCondition Condition, decimal Price, int Quantity,
    int? LengthCm, int? WidthCm, int? HeightCm, int? WeightG, IReadOnlyList<string> OemCodes)
{
    public PartDetails Details() => new(Title, Description, Condition, Price, LengthCm, WidthCm, HeightCm, WeightG, OemCodes);
}

/// <summary>Problema numa linha (ou no arquivo, linha 0), mostrado no relatório.</summary>
public sealed record PartImportIssue(int Line, string Message);

/// <summary>Código já cadastrado na loja: a linha foi pulada sem mudar a peça existente (Q33).</summary>
public sealed record PartImportSkip(int Line, string InternalCode);

/// <summary>
/// Importação de peças por planilha (RF11). Nasce validada, com as linhas boas e o relatório de erros (prévia); só depois
/// de confirmada as linhas são cadastradas, em lotes, com o progresso salvo junto de cada lote (retomar não duplica).
/// </summary>
public sealed class PartImport : ITenantOwned
{
    public const int MaxRows = 5000;
    public const int MaxFileBytes = 5 * 1024 * 1024;
    public const int FileNameMaxLength = 200;

    /// <summary>Quantos problemas o relatório guarda (o total continua contado).</summary>
    public const int MaxListedIssues = 500;

    private PartImport() { }

    public static PartImport Validate(Guid tenantId, Guid userId, string fileName, int totalRows, IReadOnlyList<PartImportRow> rows,
        IReadOnlyList<PartImportIssue> issues, int existingCodes, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant obrigatório.", nameof(tenantId));
        if (rows.Count > MaxRows) throw new ArgumentException($"No máximo {MaxRows} linhas por planilha.", nameof(rows));
        var codes = rows.Select(r => r.InternalCode).Where(c => c.Length > 0).ToList();
        if (codes.Distinct(StringComparer.Ordinal).Count() != codes.Count)
            throw new ArgumentException("Código interno repetido entre as linhas válidas.", nameof(rows));
        var name = (fileName ?? string.Empty).Trim();
        return new PartImport
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            UserId = userId,
            FileName = name.Length == 0 ? "planilha" : name.Length > FileNameMaxLength ? name[..FileNameMaxLength] : name,
            Status = PartImportStatus.Validated,
            TotalRows = totalRows,
            Rows = rows.ToList(),
            ErrorCount = issues.Count,
            Errors = issues.Take(MaxListedIssues).ToList(),
            ExistingCodesAtPreview = existingCodes,
            CreatedAt = now,
        };
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>Quem enviou: as peças nascem com o movimento de estoque em nome dele.</summary>
    public Guid UserId { get; private set; }

    public string FileName { get; private set; } = string.Empty;
    public PartImportStatus Status { get; private set; }

    /// <summary>Linhas de dados no arquivo (sem cabeçalho e linhas em branco).</summary>
    public int TotalRows { get; private set; }

    /// <summary>Linhas válidas, na ordem do arquivo; cadastradas a partir de <see cref="ProcessedRows"/>.</summary>
    public List<PartImportRow> Rows { get; private set; } = [];

    public int ErrorCount { get; private set; }
    public List<PartImportIssue> Errors { get; private set; } = [];

    /// <summary>Códigos que já existiam na prévia (informativo; o que vale é o que existir na hora do lote).</summary>
    public int ExistingCodesAtPreview { get; private set; }

    public int ProcessedRows { get; private set; }
    public int ImportedCount { get; private set; }
    public List<PartImportSkip> Skipped { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }

    public int ValidRows => Rows.Count;

    /// <summary>O usuário confirmou a prévia. Repetir não tem efeito; nada para importar termina na hora.</summary>
    public bool Confirm(DateTimeOffset now)
    {
        if (Status != PartImportStatus.Validated) return false;
        Status = PartImportStatus.Processing;
        StartedAt = now;
        if (Rows.Count == 0) Finish(now);
        return true;
    }

    /// <summary>Próximo lote a cadastrar (vazio se não está em processamento ou já acabou).</summary>
    public IReadOnlyList<PartImportRow> NextBatch(int size) =>
        Status == PartImportStatus.Processing ? Rows.Skip(ProcessedRows).Take(size).ToList() : [];

    /// <summary>Resultado de um lote, gravado na mesma transação das peças: se o lote falhar, o progresso também volta.</summary>
    public void RecordBatch(int processed, int imported, IReadOnlyList<PartImportSkip> skipped, IReadOnlyList<PartImportIssue> errors, DateTimeOffset now)
    {
        if (Status != PartImportStatus.Processing) throw new InvalidOperationException("A importação não está em andamento.");
        if (processed <= 0 || ProcessedRows + processed > Rows.Count || imported + skipped.Count + errors.Count != processed)
            throw new ArgumentException("Lote inconsistente.", nameof(processed));
        ProcessedRows += processed;
        ImportedCount += imported;
        Skipped.AddRange(skipped);
        ErrorCount += errors.Count;
        Errors.AddRange(errors.Take(Math.Max(0, MaxListedIssues - Errors.Count)));
        if (ProcessedRows == Rows.Count) Finish(now);
    }

    private void Finish(DateTimeOffset now)
    {
        Status = PartImportStatus.Completed;
        FinishedAt = now;
    }
}
