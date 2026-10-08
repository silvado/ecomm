using System.Collections.Concurrent;
using Ecommerce.Application.Tenancy;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Integration.Tests.Messaging;

/// <summary>Mensagem do spike: simula "estoque mudou" publicada pelo outbox.</summary>
public sealed record PartChanged(Guid PartId, Guid ProbeId, DateTimeOffset CommittedAt);

public sealed record HandlerObservation(
    Guid ProbeId,
    Guid? TenantInContext,
    IReadOnlyList<Guid> VisibleTenants,
    bool PartFound,
    string? DbTenantSetting,
    Guid? DbContextTenant,
    TimeSpan Latency);

public sealed class HandlerProbe
{
    private readonly ConcurrentDictionary<Guid, HandlerObservation> _observations = new();

    public void Record(HandlerObservation observation) => _observations[observation.ProbeId] = observation;

    public async Task<HandlerObservation?> WaitForAsync(Guid probeId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_observations.TryGetValue(probeId, out var observation)) return observation;
            await Task.Delay(10);
        }
        return null;
    }
}

/// <summary>
/// Handler do spike: lê dados com o RLS ativo e grava (transação aplicada pelo Wolverine),
/// para provar que o tenant já está fixado quando a conexão é aberta.
/// </summary>
public static class PartChangedHandler
{
    public static async Task Handle(PartChanged message, TenantDbContext db, ITenantContext tenant, HandlerProbe probe, CancellationToken ct)
    {
        var latency = DateTimeOffset.UtcNow - message.CommittedAt;
        var dbSetting = await db.Database.SqlQueryRaw<string>("SELECT coalesce(current_setting('app.tenant_id', true), '<null>') AS \"Value\"").SingleAsync(ct);

        var visibleTenants = await db.Parts.IgnoreQueryFilters()
            .Select(p => p.TenantId).Distinct().ToListAsync(ct);
        var part = await db.Parts.SingleOrDefaultAsync(p => p.Id == message.PartId, ct);
        part?.Rename($"Processado {message.ProbeId}");

        probe.Record(new HandlerObservation(message.ProbeId, tenant.TenantId, visibleTenants, part is not null, dbSetting, db.TenantId, latency));
    }
}
