namespace Ecommerce.Domain.Identity;

/// <summary>
/// O que um usuário pode fazer no painel. Endpoints exigem permissões, nunca papéis: um perfil novo só muda
/// <see cref="Permissions.For"/>.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711",
    Justification = "Termo do domínio; a regra mira tipos do Code Access Security, inexistente no .NET moderno.")]
public enum Permission
{
    CatalogManage,
    OrdersManage,
    SupportManage,
    VaultManage,
    PlanManage,
    UsersManage,
    DataExport,
}

public static class Permissions
{
    private static readonly IReadOnlySet<Permission> Owner = Enum.GetValues<Permission>().ToHashSet();

    /// <summary>RF07 CA1: operador sem cofre, plano, usuários e exportação.</summary>
    private static readonly IReadOnlySet<Permission> Operator =
        new HashSet<Permission> { Permission.CatalogManage, Permission.OrdersManage, Permission.SupportManage };

    public static IReadOnlySet<Permission> For(TenantRole role) => role switch
    {
        TenantRole.Owner => Owner,
        TenantRole.Operator => Operator,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };
}
