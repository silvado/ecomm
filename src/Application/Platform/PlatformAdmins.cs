namespace Ecommerce.Application.Platform;

/// <summary>Superadmin que fez login (senha ok), em qualquer etapa: troca de senha, cadastro do MFA ou código.</summary>
public sealed record PlatformAdminInfo(Guid Id, string Email, bool MustChangePassword, bool HasMfa);

/// <summary>Sessão completa (senha + MFA). <see cref="RefreshToken"/> é o valor bruto: só vai para o cookie HttpOnly.</summary>
public sealed record PlatformSession(PlatformAdminInfo Admin, Guid SessionId, DateTimeOffset ExpiresAt, string RefreshToken)
{
    public override string ToString() => $"PlatformSession({Admin.Id}, {SessionId}, ***)";
}

/// <summary>Segredo do app autenticador, mostrado uma vez durante o cadastro do MFA.</summary>
public sealed record MfaEnrollment(string Secret, string ProvisioningUri)
{
    public override string ToString() => "MfaEnrollment(***)";
}

/// <summary>MFA confirmado: a sessão já abre e os códigos de recuperação aparecem só agora.</summary>
public sealed record MfaConfirmed(PlatformSession Session, IReadOnlyList<string> RecoveryCodes)
{
    public override string ToString() => $"MfaConfirmed({Session}, ***)";
}

public enum PlatformAuthError
{
    /// <summary>Mesma resposta para e-mail inexistente, senha errada e conta bloqueada.</summary>
    InvalidCredentials,
    /// <summary>Código do app ou de recuperação errado, vencido ou já usado.</summary>
    InvalidCode,
    /// <summary>Etapa errada (ex.: confirmar MFA já ativo, entrar com MFA sem ter cadastrado).</summary>
    InvalidStep,
    InvalidSession,
    InvalidNewPassword,
}

public sealed record PlatformAuthResult<T>(T? Value, PlatformAuthError? Error) where T : class;

public static class PlatformAuthResult
{
    public static PlatformAuthResult<T> Ok<T>(T value) where T : class => new(value, null);
    public static PlatformAuthResult<T> Fail<T>(PlatformAuthError error) where T : class => new(null, error);
}

/// <summary>
/// Login do superadmin (ADR-0007): senha → (troca da senha provisória e cadastro do MFA, na primeira vez) → código → sessão.
/// Bloqueio após 5 erros (senha ou código), como nas contas de loja. Toda etapa vai para a auditoria.
/// </summary>
public interface IPlatformAdminAuth
{
    Task<PlatformAuthResult<PlatformAdminInfo>> LoginAsync(string email, string password, CancellationToken ct = default);

    /// <summary>Código do app autenticador ou código de recuperação.</summary>
    Task<PlatformAuthResult<PlatformSession>> VerifyCodeAsync(Guid adminId, string code, CancellationToken ct = default);

    Task<PlatformAuthResult<PlatformAdminInfo>> ChangePasswordAsync(Guid adminId, string currentPassword, string newPassword, CancellationToken ct = default);

    /// <summary>Gera (ou troca) o segredo ainda não confirmado. Exige a senha provisória já trocada.</summary>
    Task<PlatformAuthResult<MfaEnrollment>> StartEnrollmentAsync(Guid adminId, CancellationToken ct = default);

    Task<PlatformAuthResult<MfaConfirmed>> ConfirmEnrollmentAsync(Guid adminId, string code, CancellationToken ct = default);

    /// <summary>Sessão ainda válida para o cookie (para emitir um novo token de acesso).</summary>
    Task<PlatformAuthResult<PlatformSession>> ResumeAsync(string refreshToken, CancellationToken ct = default);

    /// <summary>Usado a cada requisição: sessão encerrada derruba o token de acesso na hora.</summary>
    Task<bool> IsSessionActiveAsync(Guid adminId, Guid sessionId, CancellationToken ct = default);

    Task LogoutAsync(string refreshToken, CancellationToken ct = default);
}

/// <summary>Quem está agindo no painel da plataforma nesta requisição (preenchido pela API; lido pela auditoria).</summary>
public sealed class PlatformActor
{
    public Guid? AdminId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? Ip { get; set; }
}

/// <summary>Auditoria do superadmin (RF07 CA3). <paramref name="details"/> não pode conter segredo: quem chama redige.</summary>
public interface IPlatformAudit
{
    Task RecordAsync(string action, Guid? tenantId = null, string? details = null, CancellationToken ct = default);
}
