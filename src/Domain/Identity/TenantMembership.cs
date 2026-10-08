namespace Ecommerce.Domain.Identity;

public enum TenantRole
{
    /// <summary>Dono: tudo, inclusive cofre, plano e usuários (RF07 CA1).</summary>
    Owner,
    /// <summary>Operador: catálogo, pedidos e atendimento (RF07 CA1).</summary>
    Operator,
}

/// <summary>Vínculo de um usuário com uma loja, com o papel que ele tem nela.</summary>
public sealed class TenantMembership
{
    private TenantMembership() { }

    public TenantMembership(Guid tenantId, Guid userId, TenantRole role, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant inválido.", nameof(tenantId));
        if (userId == Guid.Empty) throw new ArgumentException("Usuário inválido.", nameof(userId));
        TenantId = tenantId;
        UserId = userId;
        Role = role;
        CreatedAt = now;
    }

    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public TenantRole Role { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public void ChangeRole(TenantRole role) => Role = role;
}
