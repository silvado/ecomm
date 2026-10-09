namespace Ecommerce.Domain.Identity;

/// <summary>
/// Identidade de quem acessa o painel (RF07). Vive no banco <c>plataforma</c>: o login acontece antes de sabermos
/// o tenant, e um mesmo e-mail pode participar de mais de uma loja (<see cref="TenantMembership"/>).
/// </summary>
public sealed class UserAccount
{
    /// <summary>RF07 CA4: bloqueio após 5 tentativas erradas.</summary>
    public const int MaxFailedLogins = 5;

    /// <summary>HIPÓTESE (requisitos → Em aberto): a conta desbloqueia sozinha depois de 15 minutos.</summary>
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private UserAccount() { }

    public Guid Id { get; private set; }

    /// <summary>Sempre normalizado (<see cref="NormalizeEmail"/>).</summary>
    public string Email { get; private set; } = string.Empty;

    public string PasswordHash { get; private set; } = string.Empty;

    /// <summary>Senha provisória definida pelo Dono: o painel só libera a troca de senha até ela ser feita.</summary>
    public bool MustChangePassword { get; private set; }

    public int FailedLoginCount { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static UserAccount Create(string email, string passwordHash, bool mustChangePassword, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        return new UserAccount
        {
            Id = Guid.CreateVersion7(),
            Email = NormalizeEmail(email),
            PasswordHash = passwordHash,
            MustChangePassword = mustChangePassword,
            CreatedAt = now,
        };
    }

    /// <summary>
    /// A contagem de senhas erradas é incrementada de forma atômica no banco (tentativas em paralelo não podem
    /// passar do limite): ver <c>AuthService</c>.
    /// </summary>
    public bool IsLockedOut(DateTimeOffset now) => LockedUntil > now;

    /// <summary>Usado também quando o hasher pede recálculo do hash (parâmetros mais fortes).</summary>
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

    public static string NormalizeEmail(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        var value = email.Trim().ToLowerInvariant();
        var at = value.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at != value.LastIndexOf('@') || at == value.Length - 1 || value.Length > 254 || value.Any(char.IsWhiteSpace))
            throw new ArgumentException("E-mail inválido.", nameof(email));
        return value;
    }

    public override string ToString() => $"UserAccount({Id}, {Email})";
}
