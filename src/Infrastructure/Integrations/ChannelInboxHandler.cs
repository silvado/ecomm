using Ecommerce.Application.Integrations;
using Ecommerce.Domain.Integrations;
using Ecommerce.Domain.Payments;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace Ecommerce.Infrastructure.Integrations;

/// <summary>
/// Dono de uma conta externa de canal (banco plataforma). O PlatformDbContext é pedido ao contêiner só na hora: entre as
/// dependências de um handler, o Wolverine não saberia qual DbContext é o da transação.
/// </summary>
public sealed class ChannelAccountDirectory(IServiceProvider services)
{
    public Task<Guid?> TenantForAsync(string channel, string externalAccount, CancellationToken ct) =>
        services.GetRequiredService<PlatformDbContext>().ChannelAccounts.AsNoTracking()
            .Where(a => a.Channel == channel && a.ExternalAccountId == externalAccount)
            .Select(a => (Guid?)a.TenantId).SingleOrDefaultAsync(ct);
}

/// <summary>Processadores de canal registrados (um por origem).</summary>
public sealed class ChannelProcessors(IEnumerable<IChannelEventProcessor> processors)
{
    public IChannelEventProcessor? For(string source) => processors.FirstOrDefault(p => p.Source == source);
}

/// <summary>
/// Avisos de canais sem loja conhecida (RF26): recebe (inbox, chave única), roteia pelo mapa de contas e entrega no
/// escopo da loja, onde external_events (RLS, chave única por loja) garante que o processador roda uma vez por evento.
/// </summary>
public static partial class ChannelInboxHandler
{
    public static async Task<(ReceiveChannelEventOutcome, OutgoingMessages)> Handle(ReceiveChannelEvent command, TenantDbContext db, TimeProvider clock,
        CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        // Valida e normaliza (lança ArgumentException para aviso malformado).
        var inbox = new InboxEvent(command.Source, command.IdempotencyKey, command.ExternalAccount, command.Topic, command.Resource, command.Payload,
            clock.GetUtcNow());
        var inserted = await db.Database.SqlQuery<Guid>($"""
            INSERT INTO inbox.external_events (id, source, idempotency_key, external_account, topic, resource, payload, status, received_at)
            VALUES ({inbox.Id}, {inbox.Source}, {inbox.IdempotencyKey}, {inbox.ExternalAccount}, {inbox.Topic}, {inbox.Resource}, {inbox.Payload},
                    {nameof(InboxStatus.Pending)}, {inbox.ReceivedAt})
            ON CONFLICT (source, idempotency_key) DO NOTHING
            RETURNING id AS "Value"
            """).ToListAsync(ct);
        if (inserted.Count == 0) return (ReceiveChannelEventOutcome.Duplicate, outgoing);
        outgoing.Add(new RouteChannelEvent(inbox.Id));
        return (ReceiveChannelEventOutcome.Accepted, outgoing);
    }

    public static async Task Handle(RouteChannelEvent command, TenantDbContext db, ChannelAccountDirectory accounts, IMessageBus bus, TimeProvider clock,
        ILogger<RouteChannelEvent> logger, CancellationToken ct)
    {
        var inbox = await db.InboxEvents.SingleOrDefaultAsync(e => e.Id == command.InboxId, ct);
        if (inbox is not { Status: InboxStatus.Pending }) return;
        var now = clock.GetUtcNow();

        if (await accounts.TenantForAsync(inbox.Source, inbox.ExternalAccount, ct) is not { } tenantId)
        {
            inbox.MarkUnroutable(now);
            LogUnroutable(logger, inbox.Source, inbox.ExternalAccount, inbox.Id);
            return;
        }

        inbox.Route(tenantId, now);
        // Na mesma transação (outbox do Wolverine): roteado e entregue à loja juntos.
        await bus.PublishAsync(new ChannelEventReceived(inbox.Id, inbox.Source, inbox.IdempotencyKey, inbox.ExternalAccount, inbox.Topic, inbox.Resource),
            new DeliveryOptions { TenantId = tenantId.ToString() });
    }

    public static async Task<OutgoingMessages> Handle(ChannelEventReceived command, TenantDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        var id = Guid.CreateVersion7();
        var key = command.IdempotencyKey.Length > ExternalEvent.KeyMaxLength ? command.IdempotencyKey[..ExternalEvent.KeyMaxLength] : command.IdempotencyKey;
        var inserted = await db.Database.SqlQuery<Guid>($"""
            INSERT INTO external_events (id, tenant_id, source, idempotency_key, resource_id, status, received_at)
            VALUES ({id}, {db.RequireTenantId()}, {command.Source}, {key}, {command.Resource}, {nameof(ExternalEventStatus.Received)}, {clock.GetUtcNow()})
            ON CONFLICT (tenant_id, source, idempotency_key) DO NOTHING
            RETURNING id AS "Value"
            """).ToListAsync(ct);
        if (inserted.Count > 0) outgoing.Add(new ProcessChannelEvent(id, command.Topic));
        return outgoing;
    }

    public static async Task Handle(ProcessChannelEvent command, TenantDbContext db, ChannelProcessors processors, TimeProvider clock, CancellationToken ct)
    {
        var external = await db.ExternalEvents.SingleOrDefaultAsync(e => e.Id == command.ExternalEventId, ct);
        if (external is not { Status: ExternalEventStatus.Received }) return;
        var processor = processors.For(external.Source);
        var handled = processor is not null && await processor.ProcessAsync(external.Id, command.Topic, external.ResourceId, ct);
        external.Finish(handled ? ExternalEventStatus.Processed : ExternalEventStatus.Ignored, clock.GetUtcNow());
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Aviso de canal sem loja: origem {Source}, conta externa {ExternalAccount} (inbox {InboxId})")]
    private static partial void LogUnroutable(ILogger logger, string source, string externalAccount, Guid inboxId);
}

/// <summary>
/// Canal simulado (dev e testes): registra o processamento no log de integração da loja, o efeito que os testes contam
/// para provar que reentregas não repetem nada (RF26 CA2). Nunca em produção.
/// </summary>
public sealed class FakeChannelProcessor(IIntegrationLog log) : IChannelEventProcessor
{
    public const string ChannelName = "canal-simulado";

    public string Source => ChannelName;

    public async Task<bool> ProcessAsync(Guid externalEventId, string topic, string resource, CancellationToken ct = default)
    {
        if (topic == "ignorar") return false;
        await log.RecordAsync(ChannelName, $"processar aviso ({topic})", true, TimeSpan.Zero, null, resource, null, null, ct);
        return true;
    }
}
