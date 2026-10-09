using Ecommerce.Application.Identity;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Identity;

/// <summary>
/// Gestão de usuários da loja pelo Dono (RF07). O banco <c>plataforma</c> não tem RLS: toda consulta filtra pelo
/// tenant informado. Operações que dependem de contagem (limite do plano, último Dono) travam a linha do tenant,
/// para que duas requisições simultâneas não furem a regra.
/// </summary>
public sealed class UserAdministration(
    PlatformDbContext db, PasswordHashing hasher, IMembershipLookup memberships, TimeProvider clock) : IUserAdministration
{
    public async Task<IReadOnlyList<StoreUser>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        return await (from m in db.TenantMemberships
                      join u in db.UserAccounts on m.UserId equals u.Id
                      where m.TenantId == tenantId
                      orderby u.Email
                      select new StoreUser(u.Id, u.Email, m.Role, u.LockedUntil > now, u.MustChangePassword)).ToListAsync(ct);
    }

    public async Task<(AddUserOutcome Outcome, StoreUser? User)> AddAsync(
        Guid tenantId, string email, TenantRole role, string temporaryPassword, CancellationToken ct = default)
    {
        string normalized;
        try { normalized = UserAccount.NormalizeEmail(email); }
        catch (ArgumentException) { return (AddUserOutcome.InvalidEmail, null); }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var planCode = await LockTenantAsync(tenantId, ct);
        if (planCode is null) return (AddUserOutcome.PlanLimitReached, null);

        var user = await db.UserAccounts.SingleOrDefaultAsync(u => u.Email == normalized, ct);
        if (user is not null && await db.TenantMemberships.AnyAsync(m => m.TenantId == tenantId && m.UserId == user.Id, ct))
            return (AddUserOutcome.AlreadyMember, null);

        var limit = await db.PlanLimits.Where(l => l.PlanCode == planCode && l.Key == PlanLimit.Users).Select(l => (int?)l.Value).SingleOrDefaultAsync(ct);
        if (limit is not null && await db.TenantMemberships.CountAsync(m => m.TenantId == tenantId, ct) >= limit)
            return (AddUserOutcome.PlanLimitReached, null);

        var outcome = AddUserOutcome.LinkedExisting;
        var now = clock.GetUtcNow();
        if (user is null)
        {
            if (!PasswordPolicy.IsValid(temporaryPassword)) return (AddUserOutcome.InvalidPassword, null);
            user = UserAccount.Create(normalized, hasher.Hash(temporaryPassword), mustChangePassword: true, now);
            db.UserAccounts.Add(user);
            outcome = AddUserOutcome.Created;
        }

        db.TenantMemberships.Add(new TenantMembership(tenantId, user.Id, role, now));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        memberships.Invalidate(user.Id, tenantId);
        return (outcome, new StoreUser(user.Id, user.Email, role, user.IsLockedOut(now), user.MustChangePassword));
    }

    public Task<UserChangeOutcome> ChangeRoleAsync(Guid tenantId, Guid userId, TenantRole role, CancellationToken ct = default) =>
        ChangeMembershipAsync(tenantId, userId, keepsOwner: role == TenantRole.Owner, async membership =>
        {
            membership.ChangeRole(role);
            await db.SaveChangesAsync(ct);
        }, ct);

    public Task<UserChangeOutcome> RemoveAsync(Guid tenantId, Guid userId, CancellationToken ct = default) =>
        ChangeMembershipAsync(tenantId, userId, keepsOwner: false, async membership =>
        {
            db.TenantMemberships.Remove(membership);
            await db.SaveChangesAsync(ct);
        }, ct);

    public async Task<UserChangeOutcome> UnlockAsync(Guid tenantId, Guid userId, CancellationToken ct = default)
    {
        var user = await (from m in db.TenantMemberships
                          join u in db.UserAccounts on m.UserId equals u.Id
                          where m.TenantId == tenantId && m.UserId == userId
                          select u).SingleOrDefaultAsync(ct);
        if (user is null) return UserChangeOutcome.NotFound;
        user.Unlock();
        await db.SaveChangesAsync(ct);
        return UserChangeOutcome.Done;
    }

    private async Task<UserChangeOutcome> ChangeMembershipAsync(
        Guid tenantId, Guid userId, bool keepsOwner, Func<TenantMembership, Task> change, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await LockTenantAsync(tenantId, ct) is null) return UserChangeOutcome.NotFound;

        var membership = await db.TenantMemberships.SingleOrDefaultAsync(m => m.TenantId == tenantId && m.UserId == userId, ct);
        if (membership is null) return UserChangeOutcome.NotFound;

        if (membership.Role == TenantRole.Owner && !keepsOwner &&
            !await db.TenantMemberships.AnyAsync(m => m.TenantId == tenantId && m.UserId != userId && m.Role == TenantRole.Owner, ct))
            return UserChangeOutcome.LastOwner;

        await change(membership);
        await tx.CommitAsync(ct);
        memberships.Invalidate(userId, tenantId);
        return UserChangeOutcome.Done;
    }

    /// <summary>Trava a linha do tenant até o fim da transação e devolve o plano dele (nulo se não existir).</summary>
    private async Task<string?> LockTenantAsync(Guid tenantId, CancellationToken ct)
    {
        // FOR UPDATE não pode virar subconsulta: materializa em vez de compor (SingleOrDefault).
        var rows = await db.Database.SqlQuery<string>($"""SELECT plan_code AS "Value" FROM tenants WHERE id = {tenantId} FOR UPDATE""")
            .ToListAsync(ct);
        return rows.Count == 0 ? null : rows[0];
    }
}
