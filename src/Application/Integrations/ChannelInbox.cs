namespace Ecommerce.Application.Integrations;

// Entrada de avisos de canais sem loja conhecida (RF26). Fluxo: o webhook responde 2xx e invoca ReceiveChannelEvent (sem
// tenant): grava no inbox com chave única e agenda RouteChannelEvent; o roteamento acha a loja pelo mapa de contas
// (banco plataforma) e publica ChannelEventReceived já no escopo dela; ali o evento entra em external_events (RLS, chave
// única por loja) e só então vai para o processador do canal — reentrega em qualquer ponto não repete o efeito.

/// <param name="Payload">Corpo do aviso (JSON), só para diagnóstico; o processamento consulta o recurso no canal.</param>
public sealed record ReceiveChannelEvent(string Source, string IdempotencyKey, string ExternalAccount, string Topic, string Resource, string? Payload);

public enum ReceiveChannelEventOutcome
{
    Accepted,
    /// <summary>A mesma origem e chave já tinham sido recebidas: nada novo.</summary>
    Duplicate,
}

/// <summary>Sem tenant: o roteamento lê só o inbox e o mapa de contas.</summary>
public sealed record RouteChannelEvent(Guid InboxId);

/// <summary>Já no escopo da loja (DeliveryOptions.TenantId).</summary>
public sealed record ChannelEventReceived(Guid InboxId, string Source, string IdempotencyKey, string ExternalAccount, string Topic, string Resource);

/// <summary>Processa o evento gravado em external_events da loja (uma vez por evento).</summary>
public sealed record ProcessChannelEvent(Guid ExternalEventId, string Topic);

/// <summary>
/// Tratador de um canal (Mercado Livre, Meta, OLX — E2/E3): busca o recurso no canal e aplica na loja do escopo. Um por
/// <see cref="Source"/>; sem tratador registrado, o evento é marcado como ignorado.
/// </summary>
public interface IChannelEventProcessor
{
    string Source { get; }

    /// <returns>Falso = ignorado (tópico que o canal não precisa tratar).</returns>
    Task<bool> ProcessAsync(Guid externalEventId, string topic, string resource, CancellationToken ct = default);
}
