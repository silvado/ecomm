using System.Security.Cryptography;

namespace Ecommerce.Domain.Platform;

public enum DomainKind
{
    PlatformSubdomain,
    CustomWww,
    CustomApex,
}

public enum DomainVerificationStatus
{
    Pending,
    Verified,
    Failed,
}

/// <summary>Host pelo qual a loja é acessada (RF02, RF03, RF04).</summary>
public sealed class TenantDomain
{
    public const int HostMaxLength = 253;

    /// <summary>RF03 CA2: a verificação tenta por até 72 horas.</summary>
    public static readonly TimeSpan VerificationWindow = TimeSpan.FromHours(72);

    /// <summary>Nome do registro TXT que prova o controle do domínio (não é registro de e-mail — CA4).</summary>
    public const string VerificationRecordPrefix = "_ecomm-verificacao.";

    private TenantDomain() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    /// <summary>Minúsculo, sem porta e sem ponto final.</summary>
    public string Host { get; private set; } = string.Empty;
    public DomainKind Kind { get; private set; }
    public DomainVerificationStatus VerificationStatus { get; private set; }
    public DateTimeOffset? VerifiedAt { get; private set; }
    public bool IsPrimary { get; private set; }

    /// <summary>Código que a loja publica no TXT <see cref="VerificationRecordName"/> (domínio próprio).</summary>
    public string? VerificationToken { get; private set; }
    public DateTimeOffset? RequestedAt { get; private set; }
    public DateTimeOffset? LastCheckedAt { get; private set; }

    /// <summary>O que a última verificação encontrou de errado (mensagem ao lojista).</summary>
    public string? LastCheckMessage { get; private set; }

    public string VerificationRecordName => VerificationRecordPrefix + Host;

    public string VerificationRecordValue => $"ecomm-verificacao={VerificationToken}";

    public bool IsCustom => Kind != DomainKind.PlatformSubdomain;

    internal static TenantDomain PlatformSubdomain(Guid tenantId, string host, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        Host = NormalizeHost(host),
        Kind = DomainKind.PlatformSubdomain,
        VerificationStatus = DomainVerificationStatus.Verified,
        VerifiedAt = now,
        IsPrimary = true,
    };

    internal static TenantDomain Custom(Guid tenantId, string host, DomainKind kind, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        Host = host,
        Kind = kind,
        VerificationStatus = DomainVerificationStatus.Pending,
        VerificationToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
        RequestedAt = now,
    };

    internal void Verified(DateTimeOffset now)
    {
        VerificationStatus = DomainVerificationStatus.Verified;
        VerifiedAt = now;
        LastCheckedAt = now;
        LastCheckMessage = null;
    }

    /// <summary>Ainda não bate: dentro das 72 h segue pendente; depois, falhou (o lojista pede outra rodada).</summary>
    internal void CheckFailed(string message, DateTimeOffset now)
    {
        LastCheckedAt = now;
        LastCheckMessage = message;
        if (VerificationStatus == DomainVerificationStatus.Pending && now - RequestedAt > VerificationWindow)
            VerificationStatus = DomainVerificationStatus.Failed;
    }

    internal void RestartVerification(DateTimeOffset now)
    {
        if (VerificationStatus == DomainVerificationStatus.Verified) return;
        VerificationStatus = DomainVerificationStatus.Pending;
        RequestedAt = now;
        LastCheckMessage = null;
    }

    internal void SetPrimary(bool primary) => IsPrimary = primary;

    /// <summary>Normaliza o Host recebido em requisições e cadastros: minúsculo, sem porta, sem ponto final.</summary>
    public static string NormalizeHost(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var value = host.Trim().ToLowerInvariant();
        var colon = value.LastIndexOf(':');
        if (colon > 0 && !value.Contains(']', StringComparison.Ordinal)) value = value[..colon];
        return value.TrimEnd('.');
    }

    /// <summary>
    /// Domínio próprio informado pelo lojista (RF03): sem esquema, caminho nem porta; rótulos válidos; ao menos um ponto;
    /// nunca o domínio da plataforma. "www." = CustomWww; o resto = domínio raiz (CustomApex).
    /// </summary>
    public static (string Host, DomainKind Kind) ParseCustom(string? input, string platformDomain)
    {
        var text = (input ?? string.Empty).Trim().ToLowerInvariant();
        if (text.StartsWith("https://", StringComparison.Ordinal)) text = text[8..];
        else if (text.StartsWith("http://", StringComparison.Ordinal)) text = text[7..];
        text = text.TrimEnd('/').TrimEnd('.');
        var labels = text.Split('.');
        if (text.Length is 0 or > HostMaxLength || labels.Length < 2 || text.Contains('/', StringComparison.Ordinal) || text.Contains(':', StringComparison.Ordinal)
            || labels.Any(l => l.Length is 0 or > 63 || l.StartsWith('-') || l.EndsWith('-') || l.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
            || labels[^1].All(char.IsAsciiDigit))
            throw new ArgumentException("Informe o domínio como www.sualoja.com.br (sem http:// e sem barra no fim).", nameof(input));
        var platform = NormalizeHost(platformDomain);
        if (text == platform || text.EndsWith("." + platform, StringComparison.Ordinal))
            throw new ArgumentException("Esse endereço já é da plataforma. Informe o domínio que a loja registrou.", nameof(input));
        // Sem www, vale como "raiz": aceita registro A/ALIAS (raiz) ou CNAME (se for um subdomínio, ex.: loja.empresa.com.br).
        return (text, text.StartsWith("www.", StringComparison.Ordinal) ? DomainKind.CustomWww : DomainKind.CustomApex);
    }
}
