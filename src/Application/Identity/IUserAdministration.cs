using Ecommerce.Domain.Identity;

namespace Ecommerce.Application.Identity;

public sealed record StoreUser(Guid UserId, string Email, TenantRole Role, bool LockedOut, bool MustChangePassword);

public enum AddUserOutcome
{
    /// <summary>Conta nova, com a senha provisória informada pelo Dono.</summary>
    Created,
    /// <summary>O e-mail já tinha conta (em outra loja): só ganhou o vínculo; a senha dele não muda.</summary>
    LinkedExisting,
    AlreadyMember,
    PlanLimitReached,
    InvalidEmail,
    InvalidPassword,
}

public enum UserChangeOutcome
{
    Done,
    NotFound,
    /// <summary>A loja precisa de pelo menos um Dono.</summary>
    LastOwner,
}

/// <summary>Gestão dos usuários de uma loja pelo Dono (RF07 CA1, CA2). Toda operação é restrita ao tenant informado.</summary>
public interface IUserAdministration
{
    Task<IReadOnlyList<StoreUser>> ListAsync(Guid tenantId, CancellationToken ct = default);

    Task<(AddUserOutcome Outcome, StoreUser? User)> AddAsync(
        Guid tenantId, string email, TenantRole role, string temporaryPassword, CancellationToken ct = default);

    Task<UserChangeOutcome> ChangeRoleAsync(Guid tenantId, Guid userId, TenantRole role, CancellationToken ct = default);

    /// <summary>
    /// Remove o vínculo. O acesso à loja cai na próxima requisição (vínculo conferido a cada uma) e a renovação da
    /// sessão nunca devolve uma loja sem vínculo; sem nenhuma loja, a sessão é encerrada.
    /// </summary>
    Task<UserChangeOutcome> RemoveAsync(Guid tenantId, Guid userId, CancellationToken ct = default);

    Task<UserChangeOutcome> UnlockAsync(Guid tenantId, Guid userId, CancellationToken ct = default);
}
