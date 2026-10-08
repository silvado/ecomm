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
    private TenantDomain() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    /// <summary>Minúsculo, sem porta e sem ponto final.</summary>
    public string Host { get; private set; } = string.Empty;
    public DomainKind Kind { get; private set; }
    public DomainVerificationStatus VerificationStatus { get; private set; }
    public DateTimeOffset? VerifiedAt { get; private set; }
    public bool IsPrimary { get; private set; }

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

    /// <summary>Normaliza o Host recebido em requisições e cadastros: minúsculo, sem porta, sem ponto final.</summary>
    public static string NormalizeHost(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var value = host.Trim().ToLowerInvariant();
        var colon = value.LastIndexOf(':');
        if (colon > 0 && !value.Contains(']', StringComparison.Ordinal)) value = value[..colon];
        return value.TrimEnd('.');
    }
}
