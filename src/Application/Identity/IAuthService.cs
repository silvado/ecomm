using Ecommerce.Domain.Identity;

namespace Ecommerce.Application.Identity;

/// <summary>Loja que o usuário pode acessar e o papel dele nela.</summary>
public sealed record TenantAccess(Guid TenantId, string Slug, string Name, TenantRole Role)
{
    public IReadOnlySet<Permission> Permissions => Domain.Identity.Permissions.For(Role);
}

/// <summary>Sessão aberta ou renovada. <see cref="RefreshToken"/> é o valor bruto: só vai para o cookie HttpOnly.</summary>
public sealed record Session(
    Guid UserId,
    string Email,
    bool MustChangePassword,
    TenantAccess? Tenant,
    IReadOnlyList<TenantAccess> Tenants,
    string RefreshToken)
{
    public override string ToString() => $"Session({UserId}, {Tenant?.TenantId}, ***)";
}

public enum AuthError
{
    /// <summary>Mesma resposta para e-mail inexistente, senha errada e conta bloqueada (não revela contas).</summary>
    InvalidCredentials,
    /// <summary>Usuário sem vínculo com nenhuma loja.</summary>
    NoStore,
    /// <summary>Token de renovação ausente, expirado, revogado ou reutilizado: novo login.</summary>
    InvalidSession,
    /// <summary>Outra aba acabou de renovar a mesma sessão: repetir com o cookie novo.</summary>
    ConcurrentRefresh,
    /// <summary>Loja escolhida não está entre as do usuário.</summary>
    StoreNotAvailable,
    /// <summary>Senha nova fora da política.</summary>
    InvalidNewPassword,
}

public sealed record AuthResult(Session? Session, AuthError? Error)
{
    public static AuthResult Ok(Session session) => new(session, null);
    public static AuthResult Fail(AuthError error) => new(null, error);
}

/// <summary>Login e sessão do painel (RF07).</summary>
public interface IAuthService
{
    Task<AuthResult> LoginAsync(string email, string password, CancellationToken ct = default);

    /// <summary>Gira o token de renovação; mantém a loja escolhida se o vínculo ainda existir.</summary>
    Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken ct = default);

    /// <summary>Gira o token de renovação passando a sessão para outra loja do usuário.</summary>
    Task<AuthResult> SelectTenantAsync(string refreshToken, Guid tenantId, CancellationToken ct = default);

    Task LogoutAsync(string refreshToken, CancellationToken ct = default);

    /// <summary>Troca a senha, encerra todas as sessões do usuário e abre uma nova.</summary>
    Task<AuthResult> ChangePasswordAsync(Guid userId, Guid? tenantId, string currentPassword, string newPassword, CancellationToken ct = default);
}

/// <summary>
/// Vínculo usuário × loja conferido a cada requisição do painel (o token diz qual loja; o banco diz se ainda pode).
/// Implementações usam cache curto.
/// </summary>
public interface IMembershipLookup
{
    ValueTask<TenantAccess?> FindAsync(Guid userId, Guid tenantId, CancellationToken ct = default);

    void Invalidate(Guid userId, Guid tenantId);
}
