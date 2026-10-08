namespace Ecommerce.Domain.Platform;

public enum TenantStatus
{
    Onboarding,
    Active,
    /// <summary>Inadimplência: painel somente leitura, loja continua vendendo (RF38).</summary>
    ReadOnly,
    /// <summary>Inadimplência grave: loja e sincronização suspensas (RF38).</summary>
    Suspended,
    Closing,
    Closed,
}

/// <summary>
/// Loja cliente da plataforma. Vive no banco <c>plataforma</c> (catálogo de tenants — ADR-0001).
/// </summary>
public sealed class Tenant
{
    /// <summary>Banco de dados de tenant compartilhado. Outros valores apontam para bancos dedicados (RNF08).</summary>
    public const string SharedDatabase = "default";

    private readonly List<TenantDomain> _domains = [];

    private Tenant() { }

    public Guid Id { get; private set; }
    public string Slug { get; private set; } = string.Empty;
    public Cnpj Cnpj { get; private set; } = null!;
    public string LegalName { get; private set; } = string.Empty;
    public string TradeName { get; private set; } = string.Empty;
    public TenantStatus Status { get; private set; }
    public string DatabaseKey { get; private set; } = SharedDatabase;

    /// <summary>
    /// Código do plano vigente (<see cref="Plan"/>). HIPÓTESE: a loja nasce no Essencial; a assinatura (E4) passa a mudá-lo.
    /// </summary>
    public string PlanCode { get; private set; } = Plan.Essential;

    public DateTimeOffset CreatedAt { get; private set; }
    public IReadOnlyCollection<TenantDomain> Domains => _domains;

    /// <summary>Cria o tenant já com o subdomínio da plataforma verificado (RF02).</summary>
    public static Tenant Create(string slug, Cnpj cnpj, string legalName, string tradeName, string platformDomain, DateTimeOffset now)
    {
        if (!TenantSlug.IsValid(slug)) throw new ArgumentException("Identificador da loja inválido.", nameof(slug));
        ArgumentNullException.ThrowIfNull(cnpj);
        ArgumentException.ThrowIfNullOrWhiteSpace(legalName);
        ArgumentException.ThrowIfNullOrWhiteSpace(tradeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(platformDomain);

        var tenant = new Tenant
        {
            Id = Guid.CreateVersion7(),
            Slug = slug,
            Cnpj = cnpj,
            LegalName = legalName.Trim(),
            TradeName = tradeName.Trim(),
            Status = TenantStatus.Onboarding,
            CreatedAt = now,
        };
        tenant._domains.Add(TenantDomain.PlatformSubdomain(tenant.Id, $"{slug}.{platformDomain}", now));
        return tenant;
    }

    public void Activate() => Status = TenantStatus.Active;

    public void Suspend() => Status = TenantStatus.Suspended;

    public void ChangePlan(string planCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planCode);
        PlanCode = planCode;
    }

    /// <summary>A loja atende compradores (vitrine e checkout).</summary>
    public static bool IsStorefrontAvailable(TenantStatus status) => status is TenantStatus.Active or TenantStatus.ReadOnly;
}
