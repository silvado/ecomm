using Ecommerce.Application.Storage;
using Ecommerce.Domain.Privacy;

namespace Ecommerce.Application.Privacy;

/// <summary>Monta o pacote da exportação na fila (RF39). Publicado com o tenant nas opções de entrega.</summary>
public sealed record BuildDataExport(Guid ExportId);

public sealed record DataExportView(
    Guid Id, DataExportStatus Status, string RequestedByEmail, DateTimeOffset RequestedAt, DateTimeOffset? CompletedAt,
    DateTimeOffset? ExpiresAt, long? SizeBytes, string? Error, bool CanDownload);

public sealed record DataExportFile(StoredFile File, string FileName);

/// <summary>Exportação completa da loja (RF39), só o Dono. Pedir, gerar e baixar ficam no registro de acesso (RNF04).</summary>
public interface IDataExports
{
    Task<IReadOnlyList<DataExportView>> ListAsync(CancellationToken ct = default);

    /// <summary>Uma por vez: com outra na fila, devolve a mensagem para o Dono.</summary>
    Task<(DataExportView? Export, string? Error)> RequestAsync(CancellationToken ct = default);

    /// <summary>Nulo se não existir, não estiver pronta ou já tiver vencido.</summary>
    Task<DataExportFile?> DownloadAsync(Guid exportId, CancellationToken ct = default);
}
