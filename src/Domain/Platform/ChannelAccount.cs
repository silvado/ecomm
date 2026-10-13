namespace Ecommerce.Domain.Platform;

/// <summary>
/// Conta de canal de venda ligada a uma loja (banco <c>plataforma</c>): responde "o vendedor X do canal Y é da loja Z"
/// sem ler dados de nenhuma loja — é o que roteia os avisos do inbox (RF26). A conexão OAuth do canal (E2) cria a linha;
/// uma conta externa pertence a uma loja só.
/// </summary>
public sealed class ChannelAccount
{
    public const int ChannelMaxLength = 30;
    public const int ExternalAccountMaxLength = 60;

    private ChannelAccount() { }

    public ChannelAccount(Guid tenantId, string channel, string externalAccountId, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Loja obrigatória.", nameof(tenantId));
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalAccountId);
        if (channel.Length > ChannelMaxLength || externalAccountId.Length > ExternalAccountMaxLength)
            throw new ArgumentException("Canal ou conta externa grande demais.", nameof(externalAccountId));
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        Channel = channel.Trim();
        ExternalAccountId = externalAccountId.Trim();
        CreatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Channel { get; private set; } = string.Empty;
    public string ExternalAccountId { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
}
