using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Channels;

public enum ChannelConnectionStatus
{
    Active,
    /// <summary>Token vencido ou revogado: a loja precisa autorizar de novo (RF21 CA2).</summary>
    ReauthRequired,
    Disabled,
}

/// <summary>
/// Conta da loja num canal de venda (Mercado Livre, Meta, OLX — E2/E3). O mapa da plataforma (channel_accounts) aponta a
/// mesma conta externa para esta loja, para rotear os avisos do canal (RF26). Tokens ficam no cofre.
/// </summary>
public sealed class ChannelConnection : ITenantOwned
{
    public const int ChannelMaxLength = 30;
    public const int ExternalAccountMaxLength = 60;

    private ChannelConnection() { }

    public ChannelConnection(Guid tenantId, string channel, string externalAccountId, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Loja obrigatória.", nameof(tenantId));
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalAccountId);
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        Channel = channel.Trim();
        ExternalAccountId = externalAccountId.Trim();
        if (Channel.Length > ChannelMaxLength || ExternalAccountId.Length > ExternalAccountMaxLength)
            throw new ArgumentException("Canal ou conta externa grande demais.", nameof(externalAccountId));
        Status = ChannelConnectionStatus.Active;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Channel { get; private set; } = string.Empty;
    public string ExternalAccountId { get; private set; } = string.Empty;
    public ChannelConnectionStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public void ChangeStatus(ChannelConnectionStatus status) =>
        Status = Enum.IsDefined(status) ? status : throw new ArgumentOutOfRangeException(nameof(status));
}

public enum ListingStatus
{
    /// <summary>Criado aqui, ainda sem anúncio no canal.</summary>
    Pending,
    Active,
    /// <summary>Pausado porque a peça zerou (RF24 CA1); volta sozinho quando houver saldo.</summary>
    PausedBySystem,
    /// <summary>Pausado pelo lojista: o sistema não reativa (RF24 CA2).</summary>
    PausedByUser,
    Closed,
    /// <summary>A última sincronização falhou de vez (sem novas tentativas); a próxima mudança tenta de novo.</summary>
    Error,
}

/// <summary>O que a sincronização precisa fazer no canal agora.</summary>
public enum ListingAction
{
    None,
    UpdateQuantity,
    Pause,
    Resume,
}

/// <summary>
/// Anúncio de uma peça numa conexão de canal (E2). Guarda o que foi enviado por último, para só chamar o canal quando
/// algo mudou. A decisão usa o saldo lido na hora (não o do evento): eventos fora de ordem não estragam nada.
/// </summary>
public sealed class ChannelListing : ITenantOwned
{
    public const int ExternalIdMaxLength = 60;
    public const int ErrorMaxLength = 500;

    private ChannelListing() { }

    public ChannelListing(Guid tenantId, Guid partId, Guid connectionId, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty || partId == Guid.Empty || connectionId == Guid.Empty)
            throw new ArgumentException("Loja, peça e conexão obrigatórias.", nameof(partId));
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        PartId = partId;
        ConnectionId = connectionId;
        Status = ListingStatus.Pending;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid PartId { get; private set; }
    public Guid ConnectionId { get; private set; }
    public string? ExternalId { get; private set; }
    public ListingStatus Status { get; private set; }
    public int? LastPushedQuantity { get; private set; }
    public decimal? LastPushedPrice { get; private set; }
    public DateTimeOffset? LastSyncedAt { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Última conferência com o canal (reconciliação horária — RF27).</summary>
    public DateTimeOffset? LastReconciledAt { get; private set; }

    /// <summary>
    /// Situação que o anúncio deve ter no canal, segundo a verdade local: ativo só com saldo e sem pausa do lojista.
    /// Já considera a ação pendente (ex.: zerou e a sincronização ainda não rodou).
    /// </summary>
    public ListingStatus ExpectedStatus(int available) => Status switch
    {
        ListingStatus.PausedByUser => ListingStatus.PausedByUser,
        ListingStatus.Active or ListingStatus.PausedBySystem or ListingStatus.Error => available > 0 ? ListingStatus.Active : ListingStatus.PausedBySystem,
        _ => Status,
    };

    /// <summary>Conferido com o canal; <paramref name="expected"/> passa a ser a situação local (o canal foi corrigido se preciso).</summary>
    public void Reconciled(ListingStatus expected, int quantity, decimal price, bool corrected, DateTimeOffset now)
    {
        if (expected is (ListingStatus.Active or ListingStatus.PausedBySystem) && Status != ListingStatus.PausedByUser)
        {
            var wasError = Status == ListingStatus.Error;
            Status = expected;
            if (corrected || wasError || LastPushedQuantity is null) Synced(quantity, price, now);
        }
        LastReconciledAt = now;
    }

    /// <summary>O anúncio sumiu do canal (excluído lá): fica em erro até a loja decidir.</summary>
    public void MissingInChannel(DateTimeOffset now)
    {
        Failed("O anúncio não existe mais no canal.", now);
        LastReconciledAt = now;
    }

    /// <summary>RF24: decide a ação para o saldo e o preço atuais.</summary>
    public ListingAction Decide(int available, decimal price) => Status switch
    {
        ListingStatus.Active when available <= 0 => ListingAction.Pause,
        ListingStatus.Active when available != LastPushedQuantity || price != LastPushedPrice => ListingAction.UpdateQuantity,
        ListingStatus.PausedBySystem when available > 0 => ListingAction.Resume,
        // Erro: a próxima mudança tenta de novo, como se estivesse ativo.
        ListingStatus.Error when available <= 0 => ListingAction.Pause,
        ListingStatus.Error => ListingAction.UpdateQuantity,
        _ => ListingAction.None,
    };

    public void Published(string externalId, int quantity, decimal price, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(externalId) || externalId.Length > ExternalIdMaxLength)
            throw new ArgumentException("Id do anúncio no canal inválido.", nameof(externalId));
        if (Status != ListingStatus.Pending) throw new InvalidOperationException("Anúncio já publicado.");
        ExternalId = externalId.Trim();
        Status = quantity > 0 ? ListingStatus.Active : ListingStatus.PausedBySystem;
        Synced(quantity, price, now);
    }

    /// <summary>Resultado de uma sincronização bem-sucedida.</summary>
    public void Applied(ListingAction action, int quantity, decimal price, DateTimeOffset now)
    {
        Status = action switch
        {
            ListingAction.Pause => ListingStatus.PausedBySystem,
            ListingAction.Resume or ListingAction.UpdateQuantity => ListingStatus.Active,
            _ => Status,
        };
        Synced(quantity, price, now);
    }

    public void Failed(string error, DateTimeOffset now)
    {
        Status = ListingStatus.Error;
        LastError = error.Length > ErrorMaxLength ? error[..ErrorMaxLength] : error;
        LastSyncedAt = now;
    }

    /// <summary>Lojista pausa pelo painel: o sistema não reativa sozinho.</summary>
    public void PauseByUser(DateTimeOffset now)
    {
        if (Status is not (ListingStatus.Active or ListingStatus.PausedBySystem or ListingStatus.Error))
            throw new InvalidOperationException("Só anúncio ativo ou pausado pelo sistema pode ser pausado.");
        Status = ListingStatus.PausedByUser;
        LastSyncedAt = now;
    }

    /// <summary>Lojista retoma: volta a seguir o estoque (zerado, fica pausado pelo sistema).</summary>
    public void ResumeByUser(int available, decimal price, DateTimeOffset now)
    {
        if (Status != ListingStatus.PausedByUser) throw new InvalidOperationException("O anúncio não está pausado pelo lojista.");
        Status = available > 0 ? ListingStatus.Active : ListingStatus.PausedBySystem;
        Synced(available, price, now);
    }

    private void Synced(int quantity, decimal price, DateTimeOffset now)
    {
        LastPushedQuantity = quantity;
        LastPushedPrice = price;
        LastSyncedAt = now;
        LastError = null;
    }
}
