using System.Net;
using System.Text.Json;
using Ecommerce.Application.Channels;
using Ecommerce.Application.Integrations;
using Ecommerce.Domain.Channels;
using Ecommerce.Domain.Integrations;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Ecommerce.Infrastructure.Channels.MercadoLivre;

/// <summary>Aviso do Mercado Livre já lido e conferido.</summary>
public sealed record MercadoLivreNotification(string IdempotencyKey, string UserId, string Topic, string Resource, string? ApplicationId);

public enum NotificationCheck
{
    Valid,
    /// <summary>Corpo que não é um aviso do ML: 400.</summary>
    Malformed,
    /// <summary>Aviso de outro app ou de IP fora da lista configurada: 403 (não entra).</summary>
    Forbidden,
}

/// <summary>
/// Leitura dos avisos do ML (RF21 CA4). Formato em https://developers.mercadolivre.com.br/pt_br/produto-receba-notificacoes
/// (consultado em 17/10/2026, página atualizada em 14/09/2026): POST JSON com <c>resource</c>, <c>user_id</c> (número ou
/// texto), <c>topic</c>, <c>application_id</c>, <c>attempts</c>, <c>sent</c>, <c>received</c> e, em alguns tópicos,
/// <c>_id</c>/<c>id</c> e <c>actions</c>. Não há assinatura: confere-se o <c>application_id</c> (o app da plataforma) e,
/// se configurada, a lista de IPs de origem que o ML publica. O conteúdo do aviso nunca é confiado: o processamento
/// consulta o recurso na API com o token do vendedor.
/// </summary>
public static class MercadoLivreNotifications
{
    public static (NotificationCheck Check, MercadoLivreNotification? Notification) Read(string body, IPAddress? remoteIp, MercadoLivreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.NotificationIps.Count > 0 && (remoteIp is null || !options.NotificationIps.Contains(Normalize(remoteIp).ToString())))
            return (NotificationCheck.Forbidden, null);

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(body);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return (NotificationCheck.Malformed, null);
        }
        if (root.ValueKind != JsonValueKind.Object) return (NotificationCheck.Malformed, null);

        var topic = Text(root, "topic");
        var resource = Text(root, "resource");
        var userId = Text(root, "user_id");
        var application = Text(root, "application_id");
        if (topic is not { Length: > 0 and <= InboxEvent.TopicMaxLength } || resource is not { Length: > 0 and <= Domain.Payments.ExternalEvent.ResourceIdMaxLength }
            || userId is not { Length: > 0 and <= InboxEvent.AccountMaxLength } || !userId.All(char.IsAsciiDigit))
            return (NotificationCheck.Malformed, null);

        // Só avisos do app da plataforma (sem app configurado — ML simulado — qualquer um).
        if (!string.IsNullOrWhiteSpace(options.ClientId) && application != options.ClientId) return (NotificationCheck.Forbidden, null);

        // Reentrega do mesmo aviso: mesmo id, quando vem; sem id, tópico + recurso + quando o ML registrou a mudança.
        var key = Text(root, "_id") ?? Text(root, "id") ?? $"{topic}|{resource}|{Text(root, "received") ?? Text(root, "sent")}";
        if (key.Length > InboxEvent.KeyMaxLength) key = key[..InboxEvent.KeyMaxLength];
        return (NotificationCheck.Valid, new MercadoLivreNotification(key, userId, topic, resource, application));
    }

    /// <summary>Id do anúncio em <c>/items/MLB123</c> (nulo em outros recursos).</summary>
    public static string? ItemId(string resource)
    {
        const string prefix = "/items/";
        if (!resource.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var id = resource[prefix.Length..].Split('/', '?')[0];
        return id.Length is > 0 and <= ChannelListing.ExternalIdMaxLength && id.All(char.IsAsciiLetterOrDigit) ? id : null;
    }

    private static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    private static string? Text(JsonElement root, string name) => root.TryGetProperty(name, out var value)
        ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!.Trim(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        }
        : null;
}

/// <summary>
/// Processa, na loja do escopo, os avisos do ML que já passaram pelo inbox (RF26: uma vez por aviso). Nesta etapa:
/// <c>items</c> de um anúncio da loja pede a conferência dele com o canal (reconciliação — RF27); os demais tópicos
/// (vendas, perguntas, mensagens, pagamentos) ficam registrados e são ignorados até as próximas etapas do RF21.
/// </summary>
public sealed class MercadoLivreChannelProcessor(TenantDbContext db, IIntegrationLog log, IMessageBus bus) : IChannelEventProcessor
{
    public string Source => SalesChannel.MercadoLivre;

    public async Task<bool> ProcessAsync(Guid externalEventId, string topic, string resource, CancellationToken ct = default)
    {
        if (topic == "items" && MercadoLivreNotifications.ItemId(resource) is { } itemId)
        {
            var listingId = await (from l in db.ChannelListings.AsNoTracking()
                                   join c in db.ChannelConnections.AsNoTracking() on l.ConnectionId equals c.Id
                                   where c.Channel == SalesChannel.MercadoLivre && c.Status == ChannelConnectionStatus.Active && l.ExternalId == itemId
                                   select (Guid?)l.Id).FirstOrDefaultAsync(ct);
            if (listingId is { } id)
            {
                // Mudou no ML (pausa, preço, quantidade mexidos lá): confere e corrige pelo estoque da loja.
                await bus.PublishAsync(new ReconcileListing(id), new DeliveryOptions { TenantId = db.RequireTenantId().ToString() });
                await log.RecordAsync(Source, "aviso: anúncio alterado", true, TimeSpan.Zero, null, itemId, null, null, ct);
                return true;
            }
        }
        // Sem registro no log: uma loja grande recebe milhares de avisos; o aviso fica em external_events como ignorado.
        return false;
    }
}
