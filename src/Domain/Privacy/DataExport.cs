using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Privacy;

public enum DataExportStatus
{
    /// <summary>Na fila ou sendo montada.</summary>
    Pending,
    Ready,
    Failed,
    /// <summary>Passou dos 7 dias: o arquivo foi apagado.</summary>
    Expired,
}

/// <summary>
/// Exportação completa da loja (RF39): pacote ZIP com peças, fotos, pedidos, clientes e notas, em armazenamento privado.
/// O download vale por 7 dias (CA2) e passa pelo painel, que registra cada acesso. Segredos do cofre nunca entram (CA3).
/// </summary>
public sealed class DataExport : ITenantOwned
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    /// <summary>Pendente há mais tempo que isto = o processamento caiu no meio: vira falha e libera um novo pedido.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(2);

    public const int ErrorMaxLength = 300;

    private DataExport() { }

    public DataExport(Guid tenantId, Guid requestedBy, string requestedByEmail, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        RequestedBy = requestedBy;
        RequestedByEmail = requestedByEmail;
        Status = DataExportStatus.Pending;
        RequestedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid RequestedBy { get; private set; }
    public string RequestedByEmail { get; private set; } = string.Empty;
    public DataExportStatus Status { get; private set; }
    public DateTimeOffset RequestedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public string? StorageKey { get; private set; }
    public long? SizeBytes { get; private set; }
    public string? Error { get; private set; }

    public static string Key(Guid tenantId, Guid exportId) => $"{tenantId}/exportacoes/{exportId}.zip";

    public bool CanDownload(DateTimeOffset now) => Status == DataExportStatus.Ready && ExpiresAt > now;

    public void Complete(long sizeBytes, DateTimeOffset now)
    {
        if (Status != DataExportStatus.Pending) throw new InvalidOperationException("Exportação já concluída.");
        Status = DataExportStatus.Ready;
        StorageKey = Key(TenantId, Id);
        SizeBytes = sizeBytes;
        CompletedAt = now;
        ExpiresAt = now + Lifetime;
    }

    public void Fail(string error, DateTimeOffset now)
    {
        if (Status != DataExportStatus.Pending) return;
        Status = DataExportStatus.Failed;
        Error = error.Length > ErrorMaxLength ? error[..ErrorMaxLength] : error;
        CompletedAt = now;
    }
}
