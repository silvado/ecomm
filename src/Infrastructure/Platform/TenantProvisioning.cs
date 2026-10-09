using System.Security.Cryptography;
using Ecommerce.Application.Tenancy;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ecommerce.Infrastructure.Platform;

/// <summary>Domínio da plataforma, sob o qual nascem os subdomínios das lojas (RF02). HIPÓTESE Q11: nome provisório.</summary>
public sealed class PlatformOptions
{
    public const string Section = "Platform";

    /// <summary>Ex.: <c>plataforma.com.br</c> em produção, <c>localhost</c> em dev (o Caddy emite certificado local).</summary>
    public string Domain { get; set; } = string.Empty;
}

/// <summary>
/// Cria a loja com subdomínio verificado e o Dono, numa única transação no banco <c>plataforma</c>.
/// Nada é criado no banco de lojas: a aparência nasce com o padrão no primeiro acesso (sem transação entre bancos).
/// Unicidade de slug e CNPJ decidida pelos índices únicos — duas criações simultâneas não passam.
/// </summary>
public sealed class TenantProvisioning(
    PlatformDbContext db, PasswordHashing hasher, IOptions<PlatformOptions> options, TimeProvider clock) : ITenantProvisioning
{
    // Sem caracteres ambíguos (0/O, 1/l/I): a senha provisória é ditada ou copiada à mão.
    private const string ReadableCharacters = "abcdefghjkmnpqrstuvwxyz23456789";

    public async Task<CreateTenantResult> CreateAsync(CreateTenantRequest request, CancellationToken ct = default)
    {
        var platformDomain = options.Value.Domain;
        if (string.IsNullOrWhiteSpace(platformDomain)) throw new InvalidOperationException("Platform:Domain não configurado.");

        var slug = request.Slug?.Trim().ToLowerInvariant();
        if (!TenantSlug.IsValid(slug)) return Fail(CreateTenantError.InvalidSlug);
        if (!Cnpj.TryParse(request.Cnpj, out var cnpj)) return Fail(CreateTenantError.InvalidCnpj);
        if (string.IsNullOrWhiteSpace(request.LegalName) || string.IsNullOrWhiteSpace(request.TradeName) ||
            request.LegalName.Trim().Length > Tenant.LegalNameMaxLength || request.TradeName.Trim().Length > Tenant.TradeNameMaxLength)
            return Fail(CreateTenantError.InvalidName);

        string ownerEmail;
        try { ownerEmail = UserAccount.NormalizeEmail(request.OwnerEmail); }
        catch (ArgumentException) { return Fail(CreateTenantError.InvalidOwnerEmail); }
        if (request.OwnerPassword is not null && !PasswordPolicy.IsValid(request.OwnerPassword))
            return Fail(CreateTenantError.InvalidOwnerPassword);

        if (await db.Tenants.AnyAsync(t => t.Slug == slug, ct)) return Fail(CreateTenantError.SlugTaken);
        if (await db.Tenants.AnyAsync(t => t.Cnpj == cnpj, ct)) return Fail(CreateTenantError.CnpjTaken);

        var now = clock.GetUtcNow();
        // HIPÓTESE: a loja fica no ar ao ser criada (RF02 CA1); onboarding guiado e assinatura vêm no E4.
        var tenant = Tenant.Create(slug!, cnpj!, request.LegalName, request.TradeName, platformDomain, now);
        tenant.Activate();
        db.Tenants.Add(tenant);

        string? temporaryPassword = null;
        var owner = await db.UserAccounts.SingleOrDefaultAsync(u => u.Email == ownerEmail, ct);
        if (owner is null)
        {
            var password = request.OwnerPassword ?? (temporaryPassword = NewTemporaryPassword());
            owner = UserAccount.Create(ownerEmail, hasher.Hash(password), mustChangePassword: request.OwnerPassword is null, now);
            db.UserAccounts.Add(owner);
        }
        db.TenantMemberships.Add(new TenantMembership(tenant.Id, owner.Id, TenantRole.Owner, now));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ix_tenants_slug" or "ix_tenants_cnpj",
        } unique)
        {
            db.ChangeTracker.Clear();
            return Fail(unique.ConstraintName == "ix_tenants_slug" ? CreateTenantError.SlugTaken : CreateTenantError.CnpjTaken);
        }

        var host = tenant.Domains.Single(d => d.IsPrimary).Host;
        return new CreateTenantResult(new CreatedTenant(tenant.Id, tenant.Slug, host, owner.Id, owner.Email, temporaryPassword), null);
    }

    private static CreateTenantResult Fail(CreateTenantError error) => new(null, error);

    /// <summary>16 caracteres aleatórios em 4 grupos (ex.: <c>k7wd-3mqa-9xte-b2nh</c>): ~78 bits, dentro da política.</summary>
    private static string NewTemporaryPassword() =>
        string.Join('-', Enumerable.Range(0, 4).Select(_ => RandomNumberGenerator.GetString(ReadableCharacters, 4)));
}
