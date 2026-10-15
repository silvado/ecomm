namespace Ecommerce.Domain.Identity;

/// <summary>
/// Superadmin da plataforma (RF07 CA3, ADR-0007): conta separada das contas de loja, com MFA obrigatório.
/// Vive no banco <c>plataforma</c>; não tem vínculo com loja nenhuma.
/// </summary>
public sealed class PlatformAdmin
{
    private PlatformAdmin() { }

    public Guid Id { get; private set; }

    /// <summary>Sempre normalizado (<see cref="UserAccount.NormalizeEmail"/>).</summary>
    public string Email { get; private set; } = string.Empty;

    public string PasswordHash { get; private set; } = string.Empty;
    public bool MustChangePassword { get; private set; }
    public int FailedLoginCount { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }

    /// <summary>Segredo TOTP cifrado com a chave-mestra do cofre; existe desde o início do cadastro do MFA.</summary>
    public byte[]? TotpSecretCipher { get; private set; }

    /// <summary>Versão da chave-mestra usada em <see cref="TotpSecretCipher"/> (rotação do cofre).</summary>
    public string? TotpKeyVersion { get; private set; }

    /// <summary>Último passo TOTP aceito: o mesmo código (ou um anterior) não vale de novo.</summary>
    public long? TotpLastStep { get; private set; }

    /// <summary>Quando o MFA foi confirmado. Nulo = cadastro pendente: a conta só faz troca de senha e cadastro do MFA.</summary>
    public DateTimeOffset? MfaEnabledAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public bool HasMfa => MfaEnabledAt is not null;

    public static PlatformAdmin Create(string email, string passwordHash, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        return new PlatformAdmin
        {
            Id = Guid.CreateVersion7(),
            Email = UserAccount.NormalizeEmail(email),
            PasswordHash = passwordHash,
            MustChangePassword = true,
            CreatedAt = now,
        };
    }

    public bool IsLockedOut(DateTimeOffset now) => LockedUntil > now;

    public void ReplacePasswordHash(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
    }

    public void ChangePassword(string passwordHash)
    {
        ReplacePasswordHash(passwordHash);
        MustChangePassword = false;
    }

    public void Unlock()
    {
        FailedLoginCount = 0;
        LockedUntil = null;
    }

    /// <summary>Novo segredo (ainda não confirmado). Recomeçar o cadastro troca o segredo; com MFA ativo, não é permitido.</summary>
    public void StartMfaEnrollment(byte[] secretCipher, string keyVersion)
    {
        if (HasMfa) throw new InvalidOperationException("O MFA já está ativo.");
        ArgumentNullException.ThrowIfNull(secretCipher);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyVersion);
        TotpSecretCipher = secretCipher;
        TotpKeyVersion = keyVersion;
        TotpLastStep = null;
    }

    public void ConfirmMfa(long step, DateTimeOffset now)
    {
        if (TotpSecretCipher is null) throw new InvalidOperationException("Cadastro do MFA não iniciado.");
        if (HasMfa) throw new InvalidOperationException("O MFA já está ativo.");
        TotpLastStep = step;
        MfaEnabledAt = now;
    }

    public override string ToString() => $"PlatformAdmin({Id}, {Email})";
}

/// <summary>Código de recuperação do MFA: uso único, guardado só como hash (ADR-0007).</summary>
public sealed class PlatformAdminRecoveryCode
{
    public const int CountPerAdmin = 10;

    private PlatformAdminRecoveryCode() { }

    public PlatformAdminRecoveryCode(Guid adminId, byte[] codeHash, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7();
        AdminId = adminId;
        CodeHash = codeHash;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid AdminId { get; private set; }
    public byte[] CodeHash { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UsedAt { get; private set; }
}

/// <summary>
/// Sessão do painel da plataforma: o token bruto só vai no cookie HttpOnly; aqui fica o hash.
/// Duração absoluta curta (<see cref="Lifetime"/>), sem renovação além dela: depois, novo login com MFA.
/// </summary>
public sealed class PlatformAdminSession
{
    /// <summary>HIPÓTESE (ADR-0007): uma jornada de trabalho; depois, senha e código de novo.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);

    private PlatformAdminSession() { }

    public PlatformAdminSession(Guid adminId, byte[] tokenHash, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7();
        AdminId = adminId;
        TokenHash = tokenHash;
        CreatedAt = now;
        ExpiresAt = now + Lifetime;
    }

    public Guid Id { get; private set; }
    public Guid AdminId { get; private set; }
    public byte[] TokenHash { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}
