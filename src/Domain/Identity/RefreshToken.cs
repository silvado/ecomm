namespace Ecommerce.Domain.Identity;

/// <summary>
/// Token de renovação da sessão do painel. Só o hash fica no banco. Cada uso gera um novo token na mesma família;
/// reapresentar um token já usado (fora da janela de corrida entre abas) indica roubo e revoga a família inteira.
/// </summary>
public sealed class RefreshToken
{
    /// <summary>HIPÓTESE: sessão inativa por 7 dias exige novo login.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    /// <summary>Duas abas renovando ao mesmo tempo: a perdedora recebe "tente de novo" em vez de derrubar a sessão.</summary>
    public static readonly TimeSpan ConcurrentUseGrace = TimeSpan.FromSeconds(30);

    private RefreshToken() { }

    public RefreshToken(Guid userId, Guid familyId, Guid? tenantId, byte[] tokenHash, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);
        Id = Guid.CreateVersion7();
        UserId = userId;
        FamilyId = familyId;
        TenantId = tenantId;
        TokenHash = tokenHash;
        CreatedAt = now;
        ExpiresAt = now + Lifetime;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid FamilyId { get; private set; }

    /// <summary>Loja escolhida na sessão (nula até o usuário escolher, quando tem mais de uma).</summary>
    public Guid? TenantId { get; private set; }

    public byte[] TokenHash { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? UsedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
}
