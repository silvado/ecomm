using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Ecommerce.Application.Channels;
using Ecommerce.Application.Integrations;
using Ecommerce.Domain.Channels;
using Ecommerce.Infrastructure.Integrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ecommerce.Infrastructure.Channels;

/// <summary>
/// Canal simulado (dev e testes): anúncios em memória do processo. Não é um canal: serve para provar a propagação de
/// estoque e o contrato comum. <see cref="FailNext"/> simula o canal fora do ar.
/// </summary>
public sealed class FakeChannelConnector : IChannelConnector
{
    private static readonly ConcurrentDictionary<string, RemoteListing> Listings = new();
    private static readonly ConcurrentDictionary<string, int> Failures = new();
    private static readonly ConcurrentDictionary<string, bool> Removed = new();

    public string Channel => FakeChannelProcessor.ChannelName;

    /// <summary>Quantas das próximas chamadas para este anúncio falham como canal fora do ar (por anúncio: testes em paralelo não se misturam).</summary>
    public static void FailNext(string externalId, int count) => Failures[externalId] = count;

    /// <summary>Altera o anúncio "no canal" por fora (alguém mexeu lá), para testar a reconciliação.</summary>
    public static void SetRemote(RemoteListing listing)
    {
        Removed.TryRemove(listing.ExternalId, out _);
        Listings[listing.ExternalId] = listing;
    }

    /// <summary>Simula o anúncio excluído no canal: a leitura passa a devolver nulo.</summary>
    public static void RemoveRemote(string externalId)
    {
        Listings.TryRemove(externalId, out _);
        Removed[externalId] = true;
    }

    public static RemoteListing? Remote(string externalId) => Listings.TryGetValue(externalId, out var listing) ? listing : null;

    public Task<string> PublishAsync(ListingDraft draft, CancellationToken ct = default)
    {
        var id = "SIM-" + Guid.CreateVersion7().ToString("N")[..16].ToUpperInvariant();
        Listings[id] = new RemoteListing(id, draft.Quantity > 0 ? RemoteListingStatus.Active : RemoteListingStatus.Paused, draft.Quantity, draft.Price);
        return Task.FromResult(id);
    }

    public Task UpdatePriceAndQuantityAsync(string externalId, decimal price, int quantity, CancellationToken ct = default)
    {
        ThrowIfDown(externalId);
        var current = Existing(externalId);
        Listings[externalId] = current with { Price = price, Quantity = quantity };
        return Task.CompletedTask;
    }

    public Task PauseAsync(string externalId, CancellationToken ct = default)
    {
        ThrowIfDown(externalId);
        Listings[externalId] = Existing(externalId) with { Status = RemoteListingStatus.Paused };
        return Task.CompletedTask;
    }

    public Task ResumeAsync(string externalId, int quantity, CancellationToken ct = default)
    {
        ThrowIfDown(externalId);
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity), "Reativar exige quantidade maior que zero.");
        Listings[externalId] = Existing(externalId) with { Status = RemoteListingStatus.Active, Quantity = quantity };
        return Task.CompletedTask;
    }

    public Task<RemoteListing?> GetRemoteStateAsync(string externalId, CancellationToken ct = default)
    {
        ThrowIfDown(externalId);
        // Anúncio do simulado que este processo não conhece (publicado por outro processo do compose) existe, sem estado
        // conhecido; só os excluídos de propósito e ids que não são do simulado não existem.
        RemoteListing? known = Remote(externalId);
        if (known is null && externalId.StartsWith("SIM-", StringComparison.Ordinal) && !Removed.ContainsKey(externalId))
            known = Existing(externalId);
        return Task.FromResult(known);
    }

    // Memória do processo: no compose, a API publica e o Worker pode sincronizar. Anúncio que este processo não conhece
    // é tratado como existente (o simulado não tem um servidor compartilhado).
    private static RemoteListing Existing(string externalId) =>
        Remote(externalId) ?? new RemoteListing(externalId, RemoteListingStatus.Active, 0, 0m);

    private static void ThrowIfDown(string externalId)
    {
        while (Failures.TryGetValue(externalId, out var left) && left > 0)
            if (Failures.TryUpdate(externalId, left - 1, left)) throw new ChannelConnectorException("Canal simulado fora do ar.");
    }
}

/// <summary>Conector com cada chamada no log de integração da loja (RF29), ligado à peça.</summary>
public sealed class LoggedChannelConnector(IChannelConnector inner, IIntegrationLog log, Guid partId) : IChannelConnector
{
    public string Channel => inner.Channel;

    public Task<string> PublishAsync(ListingDraft draft, CancellationToken ct = default) =>
        Logged.RunAsync(log, Channel, "publicar anúncio", () => inner.PublishAsync(draft, ct), id => id, null, partId, ct);

    public Task UpdatePriceAndQuantityAsync(string externalId, decimal price, int quantity, CancellationToken ct = default) =>
        Logged.RunAsync(log, Channel, "atualizar preço e quantidade", async () => { await inner.UpdatePriceAndQuantityAsync(externalId, price, quantity, ct); return true; },
            _ => $"{externalId} · {quantity} un.", null, partId, ct);

    public Task PauseAsync(string externalId, CancellationToken ct = default) =>
        Logged.RunAsync(log, Channel, "pausar anúncio", async () => { await inner.PauseAsync(externalId, ct); return true; }, _ => externalId, null, partId, ct);

    public Task ResumeAsync(string externalId, int quantity, CancellationToken ct = default) =>
        Logged.RunAsync(log, Channel, "reativar anúncio", async () => { await inner.ResumeAsync(externalId, quantity, ct); return true; },
            _ => $"{externalId} · {quantity} un.", null, partId, ct);

    public Task<RemoteListing?> GetRemoteStateAsync(string externalId, CancellationToken ct = default) =>
        Logged.RunAsync(log, Channel, "consultar anúncio", () => inner.GetRemoteStateAsync(externalId, ct), _ => externalId, null, partId, ct);
}

/// <summary>
/// Conector da conexão: Mercado Livre (RF21) e o simulado (com Channels:UseFakeChannel); Meta e OLX entram no E2/E3.
/// O do ML é montado no escopo da loja (token do cofre, storage das fotos).
/// </summary>
public sealed class ChannelConnectorResolver(IConfiguration configuration, IServiceProvider services) : IChannelConnectorResolver
{
    public Task<IChannelConnector?> ResolveAsync(ChannelConnection connection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        IChannelConnector? connector = connection.Channel switch
        {
            SalesChannel.MercadoLivre => ActivatorUtilities.CreateInstance<MercadoLivre.MercadoLivreConnector>(services, connection),
            FakeChannelProcessor.ChannelName when configuration.GetValue<bool>("Channels:UseFakeChannel") => new FakeChannelConnector(),
            _ => null,
        };
        return Task.FromResult(connector);
    }
}

/// <summary>
/// Latência da propagação de estoque (RF24: até 60 s, meta 10 s), da mudança no estoque até o canal atualizado.
/// Histograma OpenTelemetry (<see cref="MeterName"/>) e números do processo para /internal/metricas.
/// </summary>
public sealed class ChannelSyncMetrics : IDisposable
{
    public const string MeterName = "Ecommerce.Channels";
    public static readonly TimeSpan Target = TimeSpan.FromSeconds(60);

    private readonly Meter _meter = new(MeterName);
    private readonly Histogram<double> _latency;
    private long _count;
    private long _late;
    private long _maxMilliseconds;

    public ChannelSyncMetrics() =>
        _latency = _meter.CreateHistogram<double>("channel.sync.latency", "s", "Da mudança de estoque até o anúncio atualizado no canal");

    public void Record(string channel, TimeSpan latency)
    {
        _latency.Record(latency.TotalSeconds, new KeyValuePair<string, object?>("channel", channel));
        Interlocked.Increment(ref _count);
        if (latency > Target) Interlocked.Increment(ref _late);
        var ms = (long)latency.TotalMilliseconds;
        long seen;
        while (ms > (seen = Interlocked.Read(ref _maxMilliseconds)) && Interlocked.CompareExchange(ref _maxMilliseconds, ms, seen) != seen) { }
    }

    /// <summary>Sincronizações medidas neste processo, quantas passaram de 60 s e a maior latência.</summary>
    public (long Count, long Late, TimeSpan Max) Snapshot() =>
        (Interlocked.Read(ref _count), Interlocked.Read(ref _late), TimeSpan.FromMilliseconds(Interlocked.Read(ref _maxMilliseconds)));

    public void Dispose() => _meter.Dispose();
}
