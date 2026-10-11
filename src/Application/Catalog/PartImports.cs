using Ecommerce.Domain.Catalog;

namespace Ecommerce.Application.Catalog;

// Importação de peças por planilha (RF11). Prévia síncrona (lê e valida), confirmação pela fila do tenant e cadastro em
// lotes com o progresso salvo junto de cada lote.

/// <summary>Confirma a prévia e dispara o primeiro lote.</summary>
public sealed record StartPartImport(Guid ImportId);

/// <summary>Cadastra o próximo lote; enquanto houver linhas, encadeia outro.</summary>
public sealed record ProcessPartImport(Guid ImportId);

public enum StartPartImportOutcome
{
    Started,
    NotFound,
    /// <summary>Já confirmada antes (duplo clique): nada muda.</summary>
    AlreadyStarted,
}

public sealed record PartImportView(
    Guid Id, string FileName, PartImportStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    int TotalRows, int ValidRows, int ErrorCount, int ExistingCodesAtPreview, int ProcessedRows, int ImportedCount, int SkippedCount,
    IReadOnlyList<PartImportIssue> Errors, IReadOnlyList<PartImportSkip> Skipped);

public enum PreviewOutcome
{
    Validated,
    /// <summary>Arquivo vazio, grande demais ou que não é planilha: nada foi gravado.</summary>
    Invalid,
}

public sealed record PreviewResult(PreviewOutcome Outcome, PartImportView? Import = null, string? Message = null);

public enum TemplateFormat
{
    Xlsx,
    Csv,
}

/// <summary>Importações da loja do escopo (RLS + filtro global).</summary>
public interface IPartImports
{
    /// <summary>Lê e valida o arquivo (XLSX ou CSV) e grava a prévia; nenhuma peça é criada aqui.</summary>
    Task<PreviewResult> PreviewAsync(Stream content, string? fileName, Guid userId, CancellationToken ct = default);

    Task<PartImportView?> GetAsync(Guid importId, CancellationToken ct = default);

    /// <summary>As mais recentes primeiro (para acompanhar depois de recarregar a página).</summary>
    Task<IReadOnlyList<PartImportView>> RecentAsync(int count, CancellationToken ct = default);

    byte[] Template(TemplateFormat format);
}
