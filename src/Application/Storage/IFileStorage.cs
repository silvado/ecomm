namespace Ecommerce.Application.Storage;

/// <summary>Bucket público (fotos de produto, logo) ou privado (XML/DANFE, exportações) — ADR-0005.</summary>
public enum StorageArea
{
    Public,
    Private,
}

public sealed record StoredFile(Stream Content, string ContentType, long Length);

/// <summary>
/// Arquivos em storage compatível com S3 (ADR-0005). Chaves sempre começam pelo tenant (<c>{tenantId}/...</c>):
/// quem monta a chave é o caso de uso do tenant do escopo, nunca a entrada do usuário.
/// </summary>
public interface IFileStorage
{
    Task PutAsync(StorageArea area, string key, Stream content, string contentType, CancellationToken ct = default);

    /// <summary>Nulo se não existir.</summary>
    Task<StoredFile?> GetAsync(StorageArea area, string key, CancellationToken ct = default);

    /// <summary>Não falha se já não existir.</summary>
    Task DeleteAsync(StorageArea area, string key, CancellationToken ct = default);
}
