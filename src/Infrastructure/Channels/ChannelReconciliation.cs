using System.Globalization;
using Ecommerce.Application.Channels;
using Ecommerce.Application.Integrations;
using Ecommerce.Domain.Channels;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Ecommerce.Infrastructure.Channels;

/// <summary>
/// Limites da reconciliação (RF27 CA3): por canal, quantos anúncios por lote e o intervalo entre lotes. Os limites reais de
/// cada API (ex.: Mercado Livre) entram com o conector dela; o padrão é conservador.
/// </summary>
public sealed class ReconciliationOptions
{
    public const string Section = "Channels:Reconciliation";

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Espera da primeira rodada depois que o Worker sobe.</summary>
    public TimeSpan FirstRunDelay { get; set; } = TimeSpan.FromMinutes(5);
    public int BatchSize { get; set; } = 50;
    public TimeSpan BatchInterval { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Reconciliação horária (RF27): compara situação, quantidade e preço de cada anúncio no canal com a verdade local e corrige
/// o canal; cada divergência vai para o log de integração da loja. Os anúncios são conferidos em lotes espaçados por canal.
/// </summary>
public static class ChannelReconciliationHandler
{
    public static async Task<OutgoingMessages> Handle(ReconcileChannels command, TenantDbContext db, IOptions<ReconciliationOptions> options,
        TimeProvider clock, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        var listings = await (from l in db.ChannelListings.AsNoTracking()
                              join c in db.ChannelConnections.AsNoTracking() on l.ConnectionId equals c.Id
                              where c.Status == ChannelConnectionStatus.Active && l.ExternalId != null
                                    && l.Status != ListingStatus.Pending && l.Status != ListingStatus.Closed
                              orderby c.Channel, l.LastReconciledAt
                              select new { l.Id, c.Channel }).ToListAsync(ct);
        var now = clock.GetUtcNow();
        var batch = Math.Max(1, options.Value.BatchSize);
        foreach (var channel in listings.GroupBy(l => l.Channel))
        {
            var index = 0;
            foreach (var listing in channel)
            {
                var delay = options.Value.BatchInterval * (index++ / batch);
                if (delay <= TimeSpan.Zero) outgoing.Add(new ReconcileListing(listing.Id));
                else outgoing.Schedule(new ReconcileListing(listing.Id), now + delay);
            }
        }
        return outgoing;
    }

    public static async Task Handle(ReconcileListing command, TenantDbContext db, IChannelConnectorResolver connectors, IIntegrationLog log,
        Application.Tenancy.ITenantCatalog catalog, TimeProvider clock, CancellationToken ct)
    {
        if (await ChannelSyncHandler.LockAsync(db, command.ListingId, ct) is not { ExternalId: { } externalId } listing) return;
        var connection = await db.ChannelConnections.SingleAsync(c => c.Id == listing.ConnectionId, ct);
        if (connection.Status != ChannelConnectionStatus.Active || await connectors.ResolveAsync(connection, ct) is not { } resolved) return;
        var connector = new LoggedChannelConnector(resolved, log, listing.PartId);
        var (available, price) = await ChannelSyncHandler.StockAndPriceAsync(db, catalog, listing.PartId, ct);
        var now = clock.GetUtcNow();

        var remote = await connector.GetRemoteStateAsync(externalId, ct);
        if (remote is null)
        {
            listing.MissingInChannel(now);
            await log.RecordAsync(connection.Channel, "reconciliar anúncio", false, TimeSpan.Zero, "O anúncio não existe mais no canal.", externalId, null, listing.PartId, ct);
            return;
        }

        var expected = listing.ExpectedStatus(available);
        var shouldBeActive = expected == ListingStatus.Active;
        var differences = new List<string>();
        if (remote.Status == RemoteListingStatus.Closed)
        {
            listing.MissingInChannel(now);
            await log.RecordAsync(connection.Channel, "reconciliar anúncio", false, TimeSpan.Zero, "O anúncio foi encerrado no canal.", externalId, null, listing.PartId, ct);
            return;
        }

        // Falha de comunicação sobe: a fila tenta de novo (RF28).
        if (shouldBeActive)
        {
            if (remote.Status != RemoteListingStatus.Active)
            {
                differences.Add("pausado no canal → ativo");
                await connector.ResumeAsync(externalId, available, ct);
            }
            if (remote.Quantity != available || remote.Price != price)
            {
                if (remote.Quantity != available) differences.Add(string.Create(CultureInfo.InvariantCulture, $"quantidade {remote.Quantity} → {available}"));
                if (remote.Price != price) differences.Add($"preço {Money(remote.Price)} → {Money(price)}");
                await connector.UpdatePriceAndQuantityAsync(externalId, price, available, ct);
            }
        }
        else if (remote.Status == RemoteListingStatus.Active)
        {
            differences.Add(expected == ListingStatus.PausedByUser ? "ativo no canal → pausado (pausa do lojista)" : "ativo no canal → pausado (sem estoque)");
            await connector.PauseAsync(externalId, ct);
        }

        listing.Reconciled(expected, available, price, differences.Count > 0, now);
        if (differences.Count > 0)
            await log.RecordAsync(connection.Channel, "reconciliar anúncio", true, TimeSpan.Zero, null, $"{externalId}: {string.Join("; ", differences)}", null,
                listing.PartId, ct);
    }

    /// <summary>"80,00" sem depender da cultura instalada no contêiner (o texto aparece para o lojista em Integrações).</summary>
    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');
}

/// <summary>De hora em hora, pede a reconciliação de cada loja (uma mensagem por loja, no escopo dela). Só no Worker.</summary>
public sealed partial class ChannelReconciliationScheduler(IServiceScopeFactory scopes, IOptions<ReconciliationOptions> options, TimeProvider clock,
    ILogger<ChannelReconciliationScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(options.Value.FirstRunDelay, clock, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await ScheduleAllAsync(stoppingToken);
                await Task.Delay(options.Value.Interval, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Desligando.
        }
    }

    /// <summary>Lojas com conexão de canal ativa recebem a mensagem; devolve quantas.</summary>
    public async Task<int> ScheduleAllAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var tenants = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().ChannelAccounts.AsNoTracking()
            .Select(a => a.TenantId).Distinct().ToListAsync(ct);
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        foreach (var tenantId in tenants)
        {
            // Primeiro renova os acessos que vencem em breve (RF21 CA2), depois confere os anúncios.
            await bus.PublishAsync(new RenewChannelCredentials(), new DeliveryOptions { TenantId = tenantId.ToString() });
            await bus.PublishAsync(new ReconcileChannels(), new DeliveryOptions { TenantId = tenantId.ToString() });
        }
        LogScheduled(logger, tenants.Count);
        return tenants.Count;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconciliação de canais pedida para {Tenants} lojas")]
    private static partial void LogScheduled(ILogger logger, int tenants);
}
