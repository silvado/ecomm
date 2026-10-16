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

    public const int LegalNameMaxLength = 200;
    public const int TradeNameMaxLength = 120;

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
        ArgumentException.ThrowIfNullOrWhiteSpace(platformDomain);
        if (legalName.Trim().Length > LegalNameMaxLength)
            throw new ArgumentException($"Razão social acima de {LegalNameMaxLength} caracteres.", nameof(legalName));

        var tenant = new Tenant
        {
            Id = Guid.CreateVersion7(),
            Slug = slug,
            Cnpj = cnpj,
            LegalName = legalName.Trim(),
            Status = TenantStatus.Onboarding,
            CreatedAt = now,
        };
        tenant.Rename(tradeName);
        tenant._domains.Add(TenantDomain.PlatformSubdomain(tenant.Id, $"{slug}.{platformDomain}", now));
        return tenant;
    }

    public void Activate() => Status = TenantStatus.Active;

    /// <summary>
    /// RF05 CA3: a loja em implantação abre a vitrine. Quem chama confere antes os passos obrigatórios
    /// (<see cref="Onboarding.OnboardingRules.CanPublish"/>).
    /// </summary>
    public void Publish()
    {
        if (Status is not TenantStatus.Onboarding) throw new InvalidOperationException("Só uma loja em implantação pode ser publicada.");
        Status = TenantStatus.Active;
    }

    public void Suspend() => Status = TenantStatus.Suspended;

    /// <summary>Nome fantasia exibido na loja. CNPJ e razão social não mudam por aqui (dados fiscais).</summary>
    public void Rename(string tradeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tradeName);
        var value = tradeName.Trim();
        if (value.Length > TradeNameMaxLength) throw new ArgumentException($"Nome acima de {TradeNameMaxLength} caracteres.", nameof(tradeName));
        TradeName = value;
    }

    /// <summary>
    /// Loja personalizada (RF40, Q40), contratada à parte: desde quando a aparência é cuidada pela equipe da plataforma.
    /// Nulo = o Dono edita logo, nome, cores e textos.
    /// </summary>
    public DateTimeOffset? CustomStorefrontSince { get; private set; }

    public bool IsCustomStorefront => CustomStorefrontSince is not null;

    /// <summary>Liga (mantém a data original se já estava ligada) ou desliga a loja personalizada.</summary>
    public void SetCustomStorefront(bool enabled, DateTimeOffset now) =>
        CustomStorefrontSince = enabled ? CustomStorefrontSince ?? now : null;

    /// <summary>RF03: até um domínio com www e um raiz por loja.</summary>
    public TenantDomain AddCustomDomain(string host, DomainKind kind, DateTimeOffset now)
    {
        if (kind == DomainKind.PlatformSubdomain) throw new ArgumentException("Tipo de domínio inválido.", nameof(kind));
        if (_domains.Any(d => d.Host == host)) throw new InvalidOperationException("Este domínio já está cadastrado na loja.");
        if (_domains.Any(d => d.Kind == kind))
            throw new InvalidOperationException(kind == DomainKind.CustomWww
                ? "A loja já tem um domínio com www. Remova o atual antes de cadastrar outro."
                : "A loja já tem um domínio raiz. Remova o atual antes de cadastrar outro.");
        var domain = TenantDomain.Custom(Id, host, kind, now);
        _domains.Add(domain);
        return domain;
    }

    /// <summary>Verificado (RF03 CA3): passa a abrir a loja e vira o principal (o www tem preferência sobre o raiz).</summary>
    public void DomainVerified(Guid domainId, DateTimeOffset now)
    {
        FindDomain(domainId).Verified(now);
        ChoosePrimary();
    }

    public void DomainCheckFailed(Guid domainId, string message, DateTimeOffset now) => FindDomain(domainId).CheckFailed(message, now);

    public void RestartDomainVerification(Guid domainId, DateTimeOffset now) => FindDomain(domainId).RestartVerification(now);

    /// <summary>RF03 CA5: sai na hora; sem domínio próprio verificado, o subdomínio volta a ser o principal.</summary>
    public void RemoveCustomDomain(Guid domainId)
    {
        var domain = FindDomain(domainId);
        if (!domain.IsCustom) throw new InvalidOperationException("O endereço da plataforma não pode ser removido.");
        _domains.Remove(domain);
        ChoosePrimary();
    }

    private TenantDomain FindDomain(Guid domainId) => _domains.SingleOrDefault(d => d.Id == domainId) ?? throw new KeyNotFoundException("Domínio não encontrado.");

    private void ChoosePrimary()
    {
        var primary = _domains.Where(d => d.VerificationStatus == DomainVerificationStatus.Verified)
            .OrderBy(d => d.Kind switch { DomainKind.CustomWww => 0, DomainKind.CustomApex => 1, _ => 2 })
            .First();
        foreach (var d in _domains) d.SetPrimary(d == primary);
    }

    public void ChangePlan(string planCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planCode);
        PlanCode = planCode;
    }

    /// <summary>A loja atende compradores (vitrine e checkout).</summary>
    public static bool IsStorefrontAvailable(TenantStatus status) => status is TenantStatus.Active or TenantStatus.ReadOnly;
}
