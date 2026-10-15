using System.Security.Cryptography;
using System.Text;
using Ecommerce.Application.Platform;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Identity;
using Ecommerce.Infrastructure.Vault;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ecommerce.Infrastructure.Platform;

/// <summary>Login, MFA e sessão do superadmin (ADR-0007), no banco <c>plataforma</c>.</summary>
public sealed class PlatformAdminAuth(
    PlatformDbContext db, PasswordHashing hasher, TotpSecretProtector secrets, IPlatformAudit audit, PlatformActor actor,
    TimeProvider clock) : IPlatformAdminAuth
{
    public const string Issuer = "Ecomm plataforma";

    private static readonly Lazy<string> DummyHash = new(() =>
        new PasswordHashing().Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))));

    public async Task<PlatformAuthResult<PlatformAdminInfo>> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        var admin = await FindByEmailAsync(email, ct);
        if (admin is null)
        {
            hasher.Verify(DummyHash.Value, password ?? string.Empty);
            actor.Email = SafeEmail(email);
            await audit.RecordAsync(PlatformAuditActions.LoginFailed, ct: ct);
            return PlatformAuthResult.Fail<PlatformAdminInfo>(PlatformAuthError.InvalidCredentials);
        }

        Act(admin);
        if (!await VerifyPasswordAsync(admin, password, ct))
        {
            await audit.RecordAsync(PlatformAuditActions.LoginFailed, ct: ct);
            return PlatformAuthResult.Fail<PlatformAdminInfo>(PlatformAuthError.InvalidCredentials);
        }
        return PlatformAuthResult.Ok<PlatformAdminInfo>(Info(admin));
    }

    public async Task<PlatformAuthResult<PlatformSession>> VerifyCodeAsync(Guid adminId, string code, CancellationToken ct = default)
    {
        var admin = await db.PlatformAdmins.SingleOrDefaultAsync(a => a.Id == adminId, ct);
        if (admin is null) return PlatformAuthResult.Fail<PlatformSession>(PlatformAuthError.InvalidSession);
        Act(admin);
        if (!admin.HasMfa || admin.MustChangePassword) return PlatformAuthResult.Fail<PlatformSession>(PlatformAuthError.InvalidStep);
        var now = clock.GetUtcNow();
        if (admin.IsLockedOut(now)) return PlatformAuthResult.Fail<PlatformSession>(PlatformAuthError.InvalidCode);

        if (Totp.Normalize(code) is not null)
        {
            var step = Totp.Verify(secrets.Unprotect(admin), code, now, admin.TotpLastStep);
            // Uso único decidido pelo banco: o mesmo código em duas requisições simultâneas só vale numa.
            if (step is not null && await db.PlatformAdmins
                    .Where(a => a.Id == admin.Id && (a.TotpLastStep == null || a.TotpLastStep < step))
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.TotpLastStep, step), ct) == 1)
                return PlatformAuthResult.Ok<PlatformSession>(await OpenSessionAsync(admin, PlatformAuditActions.Login, ct));
        }
        else if (RecoveryCodes.Hash(code) is { } hash)
        {
            var used = await db.PlatformAdminRecoveryCodes
                .Where(c => c.AdminId == admin.Id && c.CodeHash == hash && c.UsedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, now), ct);
            if (used == 1)
            {
                var left = await db.PlatformAdminRecoveryCodes.CountAsync(c => c.AdminId == admin.Id && c.UsedAt == null, ct);
                await audit.RecordAsync(PlatformAuditActions.RecoveryCodeUsed, details: $"restam {left}", ct: ct);
                return PlatformAuthResult.Ok<PlatformSession>(await OpenSessionAsync(admin, PlatformAuditActions.Login, ct));
            }
        }

        await CountFailureAsync(admin.Id, now, ct);
        await audit.RecordAsync(PlatformAuditActions.MfaFailed, ct: ct);
        return PlatformAuthResult.Fail<PlatformSession>(PlatformAuthError.InvalidCode);
    }

    public async Task<PlatformAuthResult<PlatformAdminInfo>> ChangePasswordAsync(
        Guid adminId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var admin = await db.PlatformAdmins.SingleOrDefaultAsync(a => a.Id == adminId, ct);
        if (admin is null) return PlatformAuthResult.Fail<PlatformAdminInfo>(PlatformAuthError.InvalidSession);
        Act(admin);
        if (!await VerifyPasswordAsync(admin, currentPassword, ct))
            return PlatformAuthResult.Fail<PlatformAdminInfo>(PlatformAuthError.InvalidCredentials);
        if (!PasswordPolicy.IsValid(newPassword) || newPassword == currentPassword)
            return PlatformAuthResult.Fail<PlatformAdminInfo>(PlatformAuthError.InvalidNewPassword);

        admin.ChangePassword(hasher.Hash(newPassword));
        await RevokeSessionsAsync(db.PlatformAdminSessions.Where(s => s.AdminId == admin.Id), ct);
        await audit.RecordAsync(PlatformAuditActions.PasswordChanged, ct: ct);
        return PlatformAuthResult.Ok<PlatformAdminInfo>(Info(admin));
    }

    public async Task<PlatformAuthResult<MfaEnrollment>> StartEnrollmentAsync(Guid adminId, CancellationToken ct = default)
    {
        var admin = await db.PlatformAdmins.SingleOrDefaultAsync(a => a.Id == adminId, ct);
        if (admin is null) return PlatformAuthResult.Fail<MfaEnrollment>(PlatformAuthError.InvalidSession);
        if (admin.HasMfa || admin.MustChangePassword) return PlatformAuthResult.Fail<MfaEnrollment>(PlatformAuthError.InvalidStep);

        var secret = Totp.NewSecret();
        var (cipher, version) = secrets.Protect(admin.Id, secret);
        admin.StartMfaEnrollment(cipher, version);
        await db.SaveChangesAsync(ct);
        return PlatformAuthResult.Ok<MfaEnrollment>(new MfaEnrollment(Base32.Encode(secret), Totp.ProvisioningUri(Issuer, admin.Email, secret)));
    }

    public async Task<PlatformAuthResult<MfaConfirmed>> ConfirmEnrollmentAsync(Guid adminId, string code, CancellationToken ct = default)
    {
        var admin = await db.PlatformAdmins.SingleOrDefaultAsync(a => a.Id == adminId, ct);
        if (admin is null) return PlatformAuthResult.Fail<MfaConfirmed>(PlatformAuthError.InvalidSession);
        Act(admin);
        if (admin.HasMfa || admin.MustChangePassword || admin.TotpSecretCipher is null)
            return PlatformAuthResult.Fail<MfaConfirmed>(PlatformAuthError.InvalidStep);
        var now = clock.GetUtcNow();
        if (admin.IsLockedOut(now)) return PlatformAuthResult.Fail<MfaConfirmed>(PlatformAuthError.InvalidCode);

        if (Totp.Verify(secrets.Unprotect(admin), code, now, admin.TotpLastStep) is not { } step)
        {
            await CountFailureAsync(admin.Id, now, ct);
            await audit.RecordAsync(PlatformAuditActions.MfaFailed, details: "cadastro", ct: ct);
            return PlatformAuthResult.Fail<MfaConfirmed>(PlatformAuthError.InvalidCode);
        }

        admin.ConfirmMfa(step, now);
        await db.PlatformAdminRecoveryCodes.Where(c => c.AdminId == admin.Id).ExecuteDeleteAsync(ct);
        var codes = RecoveryCodes.Generate(PlatformAdminRecoveryCode.CountPerAdmin);
        db.PlatformAdminRecoveryCodes.AddRange(codes.Select(c => new PlatformAdminRecoveryCode(admin.Id, RecoveryCodes.Hash(c)!, now)));
        await audit.RecordAsync(PlatformAuditActions.MfaEnrolled, ct: ct);
        return PlatformAuthResult.Ok<MfaConfirmed>(new MfaConfirmed(await OpenSessionAsync(admin, PlatformAuditActions.Login, ct), codes));
    }

    public async Task<PlatformAuthResult<PlatformSession>> ResumeAsync(string refreshToken, CancellationToken ct = default)
    {
        if (!TryHash(refreshToken, out var hash)) return PlatformAuthResult.Fail<PlatformSession>(PlatformAuthError.InvalidSession);
        var now = clock.GetUtcNow();
        var found = await (from s in db.PlatformAdminSessions
                           join a in db.PlatformAdmins on s.AdminId equals a.Id
                           where s.TokenHash == hash && s.RevokedAt == null && s.ExpiresAt > now
                           select new { Session = s, Admin = a }).AsNoTracking().SingleOrDefaultAsync(ct);
        if (found is null || !found.Admin.HasMfa) return PlatformAuthResult.Fail<PlatformSession>(PlatformAuthError.InvalidSession);
        return PlatformAuthResult.Ok<PlatformSession>(new PlatformSession(Info(found.Admin), found.Session.Id, found.Session.ExpiresAt, refreshToken));
    }

    public Task<bool> IsSessionActiveAsync(Guid adminId, Guid sessionId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        return db.PlatformAdminSessions.AnyAsync(s => s.Id == sessionId && s.AdminId == adminId && s.RevokedAt == null && s.ExpiresAt > now, ct);
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct = default)
    {
        if (TryHash(refreshToken, out var hash)) await RevokeSessionsAsync(db.PlatformAdminSessions.Where(s => s.TokenHash == hash), ct);
    }

    private async Task<PlatformSession> OpenSessionAsync(PlatformAdmin admin, string action, CancellationToken ct)
    {
        var raw = Base64Url(RandomNumberGenerator.GetBytes(32));
        var session = new PlatformAdminSession(admin.Id, Hash(raw), clock.GetUtcNow());
        db.PlatformAdminSessions.Add(session);
        admin.Unlock();
        // A auditoria grava junto com a sessão (mesmo SaveChanges).
        await audit.RecordAsync(action, ct: ct);
        return new PlatformSession(Info(admin), session.Id, session.ExpiresAt, raw);
    }

    private async Task<PlatformAdmin?> FindByEmailAsync(string email, CancellationToken ct)
    {
        string normalized;
        try { normalized = UserAccount.NormalizeEmail(email); }
        catch (ArgumentException) { return null; }
        return await db.PlatformAdmins.SingleOrDefaultAsync(a => a.Email == normalized, ct);
    }

    /// <summary>Mesma regra das contas de loja: bloqueada não é verificada (mas gasta o mesmo tempo); erros contados no banco.</summary>
    private async Task<bool> VerifyPasswordAsync(PlatformAdmin admin, string password, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (admin.IsLockedOut(now))
        {
            hasher.Verify(DummyHash.Value, password ?? string.Empty);
            return false;
        }

        var result = hasher.Verify(admin.PasswordHash, password ?? string.Empty);
        if (result == PasswordVerificationResult.Failed)
        {
            await CountFailureAsync(admin.Id, now, ct);
            return false;
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded) admin.ReplacePasswordHash(hasher.Hash(password!));
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Senha e código somam no mesmo contador: 5 erros bloqueiam por 15 minutos.</summary>
    private Task<int> CountFailureAsync(Guid adminId, DateTimeOffset now, CancellationToken ct)
    {
        var lockedUntil = now + UserAccount.LockoutDuration;
        return db.PlatformAdmins
            .Where(a => a.Id == adminId && (a.LockedUntil == null || a.LockedUntil <= now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.LockedUntil, a => a.FailedLoginCount + 1 >= UserAccount.MaxFailedLogins ? lockedUntil : a.LockedUntil)
                .SetProperty(a => a.FailedLoginCount, a => a.FailedLoginCount + 1 >= UserAccount.MaxFailedLogins ? 0 : a.FailedLoginCount + 1), ct);
    }

    private Task<int> RevokeSessionsAsync(IQueryable<PlatformAdminSession> sessions, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return sessions.Where(s => s.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
    }

    private void Act(PlatformAdmin admin)
    {
        actor.AdminId = admin.Id;
        actor.Email = admin.Email;
    }

    private static string SafeEmail(string? email)
    {
        try { return UserAccount.NormalizeEmail(email ?? string.Empty); }
        catch (ArgumentException) { return "(e-mail inválido)"; }
    }

    private static PlatformAdminInfo Info(PlatformAdmin admin) => new(admin.Id, admin.Email, admin.MustChangePassword, admin.HasMfa);

    internal static byte[] Hash(string raw) => SHA256.HashData(Encoding.ASCII.GetBytes(raw));

    private static bool TryHash(string? raw, out byte[] hash)
    {
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

/// <summary>
/// Cria superadmins (só pelo comando do migrador, no servidor): senha provisória gerada, troca e cadastro do MFA
/// obrigatórios no primeiro acesso. Devolve nulo se o e-mail for inválido ou já existir.
/// </summary>
public sealed class PlatformAdminProvisioning(PlatformDbContext db, PasswordHashing hasher, TimeProvider clock)
{
    public async Task<(PlatformAdmin Admin, string TemporaryPassword)?> CreateAsync(string email, CancellationToken ct = default)
    {
        string normalized;
        try { normalized = UserAccount.NormalizeEmail(email); }
        catch (ArgumentException) { return null; }
        if (await db.PlatformAdmins.AnyAsync(a => a.Email == normalized, ct)) return null;

        var password = TenantProvisioning.NewTemporaryPassword();
        var admin = PlatformAdmin.Create(normalized, hasher.Hash(password), clock.GetUtcNow());
        db.PlatformAdmins.Add(admin);
        db.PlatformAuditLog.Add(new PlatformAuditEntry(null, "(migrador)", PlatformAuditActions.AdminCreated, null, admin.Email, null, null, clock.GetUtcNow()));
        await db.SaveChangesAsync(ct);
        return (admin, password);
    }
}

/// <summary>Códigos de recuperação do MFA: "XXXXX-XXXXX" sem letras confundíveis; só o hash vai para o banco.</summary>
public static class RecoveryCodes
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int Length = 10;

    public static IReadOnlyList<string> Generate(int count) =>
        Enumerable.Range(0, count).Select(_ =>
        {
            var chars = RandomNumberGenerator.GetItems<char>(Alphabet, Length);
            return $"{new string(chars, 0, Length / 2)}-{new string(chars, Length / 2, Length / 2)}";
        }).ToList();

    /// <summary>Hash do código digitado (sem hífen nem espaços, maiúsculo); nulo se nem tem o formato.</summary>
    public static byte[]? Hash(string? code)
    {
        if (code is null) return null;
        var normalized = new string(code.Where(c => c is not ('-' or ' ')).ToArray()).ToUpperInvariant();
        return normalized.Length == Length && normalized.All(Alphabet.Contains)
            ? SHA256.HashData(Encoding.ASCII.GetBytes(normalized))
            : null;
    }
}

/// <summary>Segredo TOTP cifrado com a chave-mestra atual do cofre; o contexto amarra a cifra ao superadmin.</summary>
public sealed class TotpSecretProtector(IOptions<VaultOptions> options)
{
    public (byte[] Cipher, string KeyVersion) Protect(Guid adminId, byte[] secret)
    {
        var (version, key) = options.Value.CurrentMasterKey();
        return (EnvelopeCrypto.Encrypt(key, secret, Context(adminId)), version);
    }

    public byte[] Unprotect(PlatformAdmin admin)
    {
        if (admin.TotpSecretCipher is null || admin.TotpKeyVersion is null) throw new InvalidOperationException("MFA sem segredo.");
        return EnvelopeCrypto.Decrypt(options.Value.MasterKey(admin.TotpKeyVersion), admin.TotpSecretCipher, Context(admin.Id));
    }

    private static string Context(Guid adminId) => $"platform-admin-totp:{adminId}";
}
