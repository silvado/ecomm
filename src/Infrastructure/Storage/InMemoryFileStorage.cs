using System.Collections.Concurrent;
using Ecommerce.Application.Storage;

namespace Ecommerce.Infrastructure.Storage;

/// <summary>Fake de <see cref="IFileStorage"/> para testes e dev sem S3 (CLAUDE.md, regra 6).</summary>
public sealed class InMemoryFileStorage : IFileStorage
{
    private readonly ConcurrentDictionary<(StorageArea, string), (byte[] Bytes, string ContentType)> _files = new();

    public int Count => _files.Count;

    public bool Contains(StorageArea area, string key) => _files.ContainsKey((area, key));

    public async Task PutAsync(StorageArea area, string key, Stream content, string contentType, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        _files[(area, key)] = (buffer.ToArray(), contentType);
    }

    public Task<StoredFile?> GetAsync(StorageArea area, string key, CancellationToken ct = default) =>
        Task.FromResult(_files.TryGetValue((area, key), out var file)
            ? new StoredFile(new MemoryStream(file.Bytes, writable: false), file.ContentType, file.Bytes.Length)
            : null);

    public Task DeleteAsync(StorageArea area, string key, CancellationToken ct = default)
    {
        _files.TryRemove((area, key), out _);
        return Task.CompletedTask;
    }
}
