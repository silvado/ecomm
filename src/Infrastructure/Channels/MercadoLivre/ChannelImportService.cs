using Ecommerce.Application.Channels;
using Ecommerce.Domain.Channels;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Channels.MercadoLivre;

/// <summary>Acompanhamento das importações de anúncios da loja do escopo (RF11 CA3).</summary>
public sealed class ChannelImportService(TenantDbContext db) : IChannelImports
{
    public async Task<ChannelImportView?> GetAsync(Guid importId, CancellationToken ct = default) =>
        await db.ChannelImports.AsNoTracking().SingleOrDefaultAsync(i => i.Id == importId, ct) is { } import ? View(import) : null;

    public async Task<IReadOnlyList<ChannelImportView>> RecentAsync(int count, CancellationToken ct = default) =>
        (await db.ChannelImports.AsNoTracking().OrderByDescending(i => i.CreatedAt).Take(Math.Clamp(count, 1, 20)).ToListAsync(ct)).Select(View).ToList();

    private static ChannelImportView View(ChannelImport i) => new(i.Id, i.Status, i.CreatedAt, i.FinishedAt, i.TotalItems, i.Truncated, i.ProcessedItems,
        i.CreatedCount, i.LinkedCount, i.AlreadyLinkedCount, i.RejectedCount, i.Issues, i.FailureReason);
}
