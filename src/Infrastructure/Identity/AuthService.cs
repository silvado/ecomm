using System.Security.Cryptography;
using System.Text;
using Ecommerce.Application.Identity;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Platform;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Identity;

/// <summary>Login e sessão do painel (RF07 CA4), no banco <c>plataforma</c>.</summary>
public sealed class AuthService(PlatformDbContext db, PasswordHashing hasher, TimeProvider clock) : IAuthService
{
    /// <summary>Hash de uma senha qualquer: e-mail inexistente custa o mesmo tempo que senha errada.</summary>
    private static readonly Lazy<string> DummyHash = new(() =>
        new PasswordHashing().Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))));

    public async Task<AuthResult> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        var user = await FindByEmailAsync(email, ct);
        if (user is null)
        {
            hasher.Verify(DummyHash.Value, password ?? string.Empty);
            return AuthResult.Fail(AuthError.InvalidCredentials);
        }

        if (!await VerifyPasswordAsync(user, password, ct))
            return AuthResult.Fail(AuthError.InvalidCredentials);

        var tenants = await TenantsOfAsync(user.Id, ct);
        if (tenants.Count == 0) return AuthResult.Fail(AuthError.NoStore);

        return AuthResult.Ok(await IssueAsync(user, tenants.Count == 1 ? tenants[0] : null, tenants, Guid.CreateVersion7(), ct));
    }

    public Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken ct = default) =>
        RotateAsync(refreshToken, (current, tenants) =>
            tenants.SingleOrDefault(t => t.TenantId == current) ?? (tenants.Count == 1 ? tenants[0] : null), ct);

    public Task<AuthResult> SelectTenantAsync(string refreshToken, Guid tenantId, CancellationToken ct = default) =>
        RotateAsync(refreshToken, (_, tenants) => tenants.SingleOrDefault(t => t.TenantId == tenantId), ct, requireTenant: true);

    public async Task LogoutAsync(string refreshToken, CancellationToken ct = default)
    {
        if (!TryHash(refreshToken, out var hash)) return;
        var familyId = await db.RefreshTokens.Where(t => t.TokenHash == hash).Select(t => (Guid?)t.FamilyId).SingleOrDefaultAsync(ct);
        if (familyId is not null) await RevokeAsync(db.RefreshTokens.Where(t => t.FamilyId == familyId), ct);
    }

    public async Task<AuthResult> ChangePasswordAsync(
        Guid userId, Guid? tenantId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var user = await db.UserAccounts.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !await VerifyPasswordAsync(user, currentPassword, ct))
            return AuthResult.Fail(AuthError.InvalidCredentials);
        if (!PasswordPolicy.IsValid(newPassword) || newPassword == currentPassword)
            return AuthResult.Fail(AuthError.InvalidNewPassword);

        user.ChangePassword(hasher.Hash(newPassword));
        await RevokeAsync(db.RefreshTokens.Where(t => t.UserId == userId), ct);

        var tenants = await TenantsOfAsync(userId, ct);
        var tenant = tenants.SingleOrDefault(t => t.TenantId == tenantId) ?? (tenants.Count == 1 ? tenants[0] : null);
        return AuthResult.Ok(await IssueAsync(user, tenant, tenants, Guid.CreateVersion7(), ct));
    }

    private async Task<UserAccount?> FindByEmailAsync(string email, CancellationToken ct)
    {
        string normalized;
        try { normalized = UserAccount.NormalizeEmail(email); }
        catch (ArgumentException) { return null; }
        return await db.UserAccounts.SingleOrDefaultAsync(u => u.Email == normalized, ct);
    }

    /// <summary>
    /// Confere a senha respeitando o bloqueio. Conta bloqueada nunca é verificada de verdade, mas gasta o mesmo tempo.
    /// A contagem de erros é atômica: 5 tentativas em paralelo bloqueiam a conta como 5 em sequência.
    /// </summary>
    private async Task<bool> VerifyPasswordAsync(UserAccount user, string password, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (user.IsLockedOut(now))
        {
            hasher.Verify(DummyHash.Value, password ?? string.Empty);
            return false;
        }

        var result = hasher.Verify(user.PasswordHash, password ?? string.Empty);
        if (result == PasswordVerificationResult.Failed)
        {
            var lockedUntil = now + UserAccount.LockoutDuration;
            await db.UserAccounts
                .Where(u => u.Id == user.Id && (u.LockedUntil == null || u.LockedUntil <= now))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.LockedUntil, u => u.FailedLoginCount + 1 >= UserAccount.MaxFailedLogins ? lockedUntil : u.LockedUntil)
                    .SetProperty(u => u.FailedLoginCount, u => u.FailedLoginCount + 1 >= UserAccount.MaxFailedLogins ? 0 : u.FailedLoginCount + 1), ct);
            return false;
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            user.ReplacePasswordHash(hasher.Hash(password!));
        user.Unlock();
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<AuthResult> RotateAsync(
        string refreshToken, Func<Guid?, IReadOnlyList<TenantAccess>, TenantAccess?> chooseTenant, CancellationToken ct, bool requireTenant = false)
    {
        if (!TryHash(refreshToken, out var hash)) return AuthResult.Fail(AuthError.InvalidSession);
        var token = await db.RefreshTokens.AsNoTracking().SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        var now = clock.GetUtcNow();
        if (token is null || token.RevokedAt is not null || token.ExpiresAt <= now)
            return AuthResult.Fail(AuthError.InvalidSession);

        if (token.UsedAt is not null)
        {
            if (now - token.UsedAt <= RefreshToken.ConcurrentUseGrace) return AuthResult.Fail(AuthError.ConcurrentRefresh);
            // Token antigo reapresentado: alguém tem uma cópia. Derruba a sessão inteira.
            await RevokeAsync(db.RefreshTokens.Where(t => t.FamilyId == token.FamilyId), ct);
            return AuthResult.Fail(AuthError.InvalidSession);
        }

        var user = await db.UserAccounts.SingleOrDefaultAsync(u => u.Id == token.UserId, ct);
        var tenants = user is null ? [] : await TenantsOfAsync(user.Id, ct);
        if (user is null || tenants.Count == 0)
        {
            await RevokeAsync(db.RefreshTokens.Where(t => t.FamilyId == token.FamilyId), ct);
            return AuthResult.Fail(AuthError.InvalidSession);
        }

        var tenant = chooseTenant(token.TenantId, tenants);
        if (requireTenant && tenant is null) return AuthResult.Fail(AuthError.StoreNotAvailable);

        // Uso único decidido pelo banco: de duas renovações simultâneas, só uma vence.
        var claimed = await db.RefreshTokens
            .Where(t => t.Id == token.Id && t.UsedAt == null && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);
        if (claimed == 0) return AuthResult.Fail(AuthError.ConcurrentRefresh);

        return AuthResult.Ok(await IssueAsync(user, tenant, tenants, token.FamilyId, ct));
    }

    private async Task<Session> IssueAsync(
        UserAccount user, TenantAccess? tenant, IReadOnlyList<TenantAccess> tenants, Guid familyId, CancellationToken ct)
    {
        var raw = Base64Url(RandomNumberGenerator.GetBytes(32));
        db.RefreshTokens.Add(new RefreshToken(user.Id, familyId, tenant?.TenantId, Hash(raw), clock.GetUtcNow()));
        await db.SaveChangesAsync(ct);
        return new Session(user.Id, user.Email, user.MustChangePassword, tenant, tenants, raw);
    }

    private async Task<List<TenantAccess>> TenantsOfAsync(Guid userId, CancellationToken ct) =>
        await (from m in db.TenantMemberships
               join t in db.Tenants on m.TenantId equals t.Id
               where m.UserId == userId && t.Status != TenantStatus.Closed
               orderby t.TradeName
               select new TenantAccess(t.Id, t.Slug, t.TradeName, m.Role)).ToListAsync(ct);

    private Task<int> RevokeAsync(IQueryable<RefreshToken> tokens, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return tokens.Where(t => t.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    internal static byte[] Hash(string raw) => SHA256.HashData(Encoding.ASCII.GetBytes(raw));

    private static bool TryHash(string? raw, out byte[] hash)
    {
        // 32 bytes em base64url = 43 caracteres; qualquer outra coisa nem chega ao banco.
        if (raw is not { Length: 43 } || raw.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
        {
            hash = [];
            return false;
        }
        hash = Hash(raw);
        return true;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
