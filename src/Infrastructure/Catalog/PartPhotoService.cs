using Ecommerce.Application.Catalog;
using Ecommerce.Application.Storage;
using Ecommerce.Domain.Catalog;
using Ecommerce.Domain.Common;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Catalog;

/// <summary>
/// Fotos das peças (RF08). A conversão roda fora de transação; o registro da foto trava a linha da peça,
/// para que envios simultâneos não passem do limite de 20. Arquivos gravados sem registro são apagados.
/// Fotos não mexem em estoque: por isso não passam pelo Wolverine.
/// </summary>
public sealed class PartPhotoService(TenantDbContext db, IFileStorage storage, TimeProvider clock) : IPartPhotos
{
    public async Task<PhotoResult> AddAsync(Guid partId, Stream content, CancellationToken ct = default)
    {
        var tenantId = db.RequireTenantId();

        // Lê no máximo 1 byte além do limite: arquivo grande é recusado sem ser carregado inteiro.
        var buffer = new byte[PartPhoto.MaxUploadBytes + 1];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = await content.ReadAsync(buffer.AsMemory(length), ct)) > 0) length += read;
        if (length == 0) return Invalid("Envie a imagem da foto.");
        if (length > PartPhoto.MaxUploadBytes) return Invalid("A foto pode ter no máximo 10 MB.");
        var contentType = ImageSignature.DetectContentType(buffer.AsSpan(0, Math.Min(length, ImageSignature.HeaderLength)));
        if (contentType is null) return Invalid("Formato não aceito: envie JPG, PNG ou WebP.");

        // Confere antes de gastar CPU na conversão (a contagem definitiva é feita com a peça travada).
        var count = await db.Parts.Where(p => p.Id == partId).Select(p => (int?)p.Photos.Count).SingleOrDefaultAsync(ct);
        if (count is null) return new PhotoResult(PhotoOutcome.NotFound);
        if (count >= Part.MaxPhotos) return TooMany();

        var original = buffer.AsSpan(0, length).ToArray();
        var (processed, error) = await PhotoProcessor.ProcessAsync(original, ct);
        if (processed is null) return Invalid(error!);

        var photoId = Guid.CreateVersion7();
        var keys = new List<(StorageArea Area, string Key)>();
        try
        {
            await PutAsync(StorageArea.Private, PartPhoto.OriginalKey(tenantId, partId, photoId), original, contentType, keys, ct);
            foreach (var (size, webp) in processed.WebpBySize)
                await PutAsync(StorageArea.Public, PartPhoto.PublicKey(tenantId, partId, photoId, size), webp, "image/webp", keys, ct);

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var part = await LockedPartAsync(partId, ct);
            if (part is null)
            {
                await DeleteAsync(keys);
                return new PhotoResult(PhotoOutcome.NotFound);
            }
            if (part.Photos.Count >= Part.MaxPhotos)
            {
                await DeleteAsync(keys);
                return TooMany();
            }
            part.AddPhoto(photoId, contentType, clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new PhotoResult(PhotoOutcome.Done, photoId);
        }
        catch
        {
            await DeleteAsync(keys);
            throw;
        }
    }

    public async Task<PhotoResult> RemoveAsync(Guid partId, Guid photoId, CancellationToken ct = default)
    {
        var tenantId = db.RequireTenantId();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var part = await LockedPartAsync(partId, ct);
        if (part is null || part.Photos.All(p => p.Id != photoId)) return new PhotoResult(PhotoOutcome.NotFound);
        try { part.RemovePhoto(photoId, clock.GetUtcNow()); }
        catch (InvalidOperationException e) { return new PhotoResult(PhotoOutcome.Conflict, null, e.Message); }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Só depois de gravar: a foto já não é servida; os arquivos saem do storage.
        await DeleteAsync([(StorageArea.Private, PartPhoto.OriginalKey(tenantId, partId, photoId)),
            .. PartPhoto.Sizes.Select(size => (StorageArea.Public, PartPhoto.PublicKey(tenantId, partId, photoId, size)))]);
        return new PhotoResult(PhotoOutcome.Done, photoId);
    }

    public async Task<PhotoResult> ReorderAsync(Guid partId, IReadOnlyList<Guid> photoIds, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var part = await LockedPartAsync(partId, ct);
        if (part is null) return new PhotoResult(PhotoOutcome.NotFound);
        try { part.ReorderPhotos(photoIds ?? [], clock.GetUtcNow()); }
        catch (ArgumentException e) { return Invalid(e.Message.Split(" (Parameter", 2)[0]); }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new PhotoResult(PhotoOutcome.Done);
    }

    public async Task<StoredFile?> GetAsync(Guid partId, Guid photoId, int size, CancellationToken ct = default)
    {
        if (!PartPhoto.Sizes.Contains(size)) return null;
        var tenantId = db.RequireTenantId();
        if (!await db.PartPhotos.AnyAsync(p => p.Id == photoId && p.PartId == partId, ct)) return null;
        return await storage.GetAsync(StorageArea.Public, PartPhoto.PublicKey(tenantId, partId, photoId, size), ct);
    }

    /// <summary>Trava a peça até o fim da transação e a carrega com as fotos (nulo se não for desta loja).</summary>
    private async Task<Part?> LockedPartAsync(Guid partId, CancellationToken ct)
    {
        var locked = await db.Database.SqlQuery<Guid>($"""SELECT id AS "Value" FROM parts WHERE id = {partId} FOR UPDATE""").ToListAsync(ct);
        if (locked.Count == 0) return null;
        return await db.Parts.Include(p => p.Photos).SingleAsync(p => p.Id == partId, ct);
    }

    private async Task PutAsync(StorageArea area, string key, byte[] bytes, string contentType, List<(StorageArea, string)> written, CancellationToken ct)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        await storage.PutAsync(area, key, stream, contentType, ct);
        written.Add((area, key));
    }

    private async Task DeleteAsync(IEnumerable<(StorageArea Area, string Key)> keys)
    {
        foreach (var (area, key) in keys) await storage.DeleteAsync(area, key, CancellationToken.None);
    }

    private static PhotoResult Invalid(string message) => new(PhotoOutcome.Invalid, null, message);

    private static PhotoResult TooMany() => new(PhotoOutcome.Conflict, null, $"A peça já tem {Part.MaxPhotos} fotos.");
}
