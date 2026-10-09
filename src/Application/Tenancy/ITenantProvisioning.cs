namespace Ecommerce.Application.Tenancy;

/// <summary>
/// Dados para criar uma loja (RF01, RF02). <see cref="OwnerPassword"/> só é usado pela semente de desenvolvimento;
/// nulo = senha provisória gerada, com troca obrigatória no primeiro acesso.
/// </summary>
public sealed record CreateTenantRequest(
    string Slug, string Cnpj, string LegalName, string TradeName, string OwnerEmail, string? OwnerPassword = null)
{
    public override string ToString() => $"CreateTenantRequest({Slug}, {Cnpj}, {OwnerEmail}, ***)";
}

public enum CreateTenantError
{
    InvalidSlug,
    SlugTaken,
    InvalidCnpj,
    CnpjTaken,
    InvalidName,
    InvalidOwnerEmail,
    InvalidOwnerPassword,
}

/// <summary>
/// Loja criada. <see cref="TemporaryPassword"/> só vem quando o Dono ganhou conta nova com senha gerada:
/// é mostrada uma única vez a quem criou a loja e nunca é gravada em log.
/// </summary>
public sealed record CreatedTenant(Guid TenantId, string Slug, string Host, Guid OwnerId, string OwnerEmail, string? TemporaryPassword)
{
    public override string ToString() => $"CreatedTenant({TenantId}, {Host}, {OwnerEmail}, ***)";
}

public sealed record CreateTenantResult(CreatedTenant? Tenant, CreateTenantError? Error);

/// <summary>Criação de lojas. Hoje usada pelo comando do migrador; no E4, pelo painel de superadmin.</summary>
public interface ITenantProvisioning
{
    Task<CreateTenantResult> CreateAsync(CreateTenantRequest request, CancellationToken ct = default);
}
