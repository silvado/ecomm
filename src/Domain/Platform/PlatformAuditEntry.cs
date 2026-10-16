namespace Ecommerce.Domain.Platform;

/// <summary>
/// Registro de auditoria do superadmin (RF07 CA3, ADR-0007): somente inclusão — o papel da aplicação não altera nem apaga.
/// <see cref="Details"/> já chega redigido (sem senha, token ou segredo).
/// </summary>
public sealed class PlatformAuditEntry
{
    public const int ActionMaxLength = 60;
    public const int DetailsMaxLength = 1000;

    private PlatformAuditEntry() { }

    public PlatformAuditEntry(Guid? adminId, string adminEmail, string action, Guid? tenantId, string? details, string? ip,
        string? correlationId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        Id = Guid.CreateVersion7();
        AdminId = adminId;
        AdminEmail = adminEmail;
        Action = action;
        TenantId = tenantId;
        Details = details is { Length: > DetailsMaxLength } ? details[..DetailsMaxLength] : details;
        Ip = ip;
        CorrelationId = correlationId;
        At = now;
    }

    public Guid Id { get; private set; }

    /// <summary>Nulo em tentativa de login com e-mail que não é de superadmin.</summary>
    public Guid? AdminId { get; private set; }

    /// <summary>Guardado à parte: o registro continua legível mesmo se a conta deixar de existir.</summary>
    public string AdminEmail { get; private set; } = string.Empty;

    public string Action { get; private set; } = string.Empty;
    public Guid? TenantId { get; private set; }
    public string? Details { get; private set; }
    public string? Ip { get; private set; }
    public string? CorrelationId { get; private set; }
    public DateTimeOffset At { get; private set; }
}

/// <summary>Ações registradas na auditoria da plataforma (texto estável: vai para o banco e para os filtros da tela).</summary>
public static class PlatformAuditActions
{
    public const string AdminCreated = "superadmin.criado";
    public const string Login = "login";
    public const string LoginFailed = "login.falhou";
    public const string MfaFailed = "mfa.falhou";
    public const string MfaEnrolled = "mfa.cadastrado";
    public const string RecoveryCodeUsed = "mfa.codigo-de-recuperacao";
    public const string PasswordChanged = "senha.trocada";
    public const string TenantViewed = "loja.visualizada";
    public const string TenantCreated = "loja.criada";
    public const string PlanChanged = "loja.plano";
    public const string TenantSuspended = "loja.suspensa";
    public const string TenantReactivated = "loja.reativada";
    public const string CustomStorefrontChanged = "loja.personalizada";
    public const string ThemeUpdated = "loja.tema";
    public const string PageSaved = "loja.pagina";
    public const string PageRemoved = "loja.pagina.removida";
}
