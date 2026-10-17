using Ecommerce.Application.Channels;
using Ecommerce.Domain.Channels;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ecommerce.Infrastructure.Channels.MercadoLivre;

/// <summary>
/// RF21 CA2: renova antes de vencer os acessos da loja do escopo. Rodando de hora em hora com janela de 90 minutos, cada
/// acesso de 6 h é renovado umas 4 vezes por dia — o que também mantém vivo o refresh token (6 meses) e descobre cedo
/// uma revogação. ML fora do ar sobe a exceção: a fila tenta de novo (RF28).
/// </summary>
public static class ChannelCredentialHandler
{
    public static async Task Handle(RenewChannelCredentials command, TenantDbContext db, MercadoLivreCredentials credentials,
        IOptions<MercadoLivreOptions> options, TimeProvider clock, CancellationToken ct)
    {
        var due = clock.GetUtcNow() + options.Value.RenewBefore;
        var ids = await db.ChannelConnections.AsNoTracking()
            .Where(c => c.Channel == SalesChannel.MercadoLivre && c.Status == ChannelConnectionStatus.Active && c.TokenExpiresAt != null && c.TokenExpiresAt < due)
            .Select(c => c.Id).ToListAsync(ct);
        foreach (var id in ids) await credentials.RenewIfDueAsync(id, ct);
    }
}
