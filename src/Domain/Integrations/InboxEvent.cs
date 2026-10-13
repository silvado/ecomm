namespace Ecommerce.Domain.Integrations;

public enum InboxStatus
{
    /// <summary>Gravado, aguardando o roteamento para a loja.</summary>
    Pending,
    /// <summary>Loja encontrada; o evento seguiu para o escopo dela.</summary>
    Routed,
    /// <summary>Nenhuma loja conectada a esta conta externa.</summary>
    Unroutable,
}

/// <summary>
/// Aviso de canal que chega antes de se saber a loja (RF26; ex.: o Mercado Livre identifica o vendedor, não a loja).
/// Fica no schema <c>inbox</c>, sem RLS — exceção documentada no ADR-0001: não guarda dado de loja além do que o canal
/// mandou e só ganha <see cref="TenantId"/> depois do roteamento. A chave (origem + chave) descarta reentregas.
/// </summary>
public sealed class InboxEvent
{
    public const int SourceMaxLength = 30;
    public const int KeyMaxLength = 200;
    public const int AccountMaxLength = 60;
    public const int TopicMaxLength = 60;
    public const int ResourceMaxLength = 300;
    public const int PayloadMaxLength = 16 * 1024;

    private InboxEvent() { }

    public InboxEvent(string source, string idempotencyKey, string externalAccount, string topic, string resource, string? payload, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7();
        Source = Required(source, SourceMaxLength, nameof(source));
        IdempotencyKey = Required(idempotencyKey, KeyMaxLength, nameof(idempotencyKey));
        ExternalAccount = Required(externalAccount, AccountMaxLength, nameof(externalAccount));
        Topic = Required(topic, TopicMaxLength, nameof(topic));
        Resource = Required(resource, ResourceMaxLength, nameof(resource));
        if (payload is { Length: > PayloadMaxLength }) throw new ArgumentException("Aviso grande demais.", nameof(payload));
        Payload = payload;
        Status = InboxStatus.Pending;
        ReceivedAt = now;
    }

    public Guid Id { get; private set; }
    public string Source { get; private set; } = string.Empty;
    public string IdempotencyKey { get; private set; } = string.Empty;

    /// <summary>Conta no canal (ex.: user_id do vendedor no Mercado Livre).</summary>
    public string ExternalAccount { get; private set; } = string.Empty;

    public string Topic { get; private set; } = string.Empty;

    /// <summary>Recurso a consultar no canal (o aviso não é a verdade: o processamento busca o recurso — RF21).</summary>
    public string Resource { get; private set; } = string.Empty;

    public string? Payload { get; private set; }
    public InboxStatus Status { get; private set; }
    public Guid? TenantId { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    public DateTimeOffset? RoutedAt { get; private set; }

    public void Route(Guid tenantId, DateTimeOffset now)
    {
        if (Status != InboxStatus.Pending) throw new InvalidOperationException("Aviso já roteado.");
        if (tenantId == Guid.Empty) throw new ArgumentException("Loja obrigatória.", nameof(tenantId));
        TenantId = tenantId;
        Status = InboxStatus.Routed;
        RoutedAt = now;
    }

    public void MarkUnroutable(DateTimeOffset now)
    {
        if (Status != InboxStatus.Pending) throw new InvalidOperationException("Aviso já roteado.");
        Status = InboxStatus.Unroutable;
        RoutedAt = now;
    }

    private static string Required(string value, int max, string name)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0 || text.Length > max || text.Any(char.IsControl)) throw new ArgumentException($"Campo {name} inválido.", name);
        return text;
    }
}
