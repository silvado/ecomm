using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Ecommerce.Application.Vault;
using Ecommerce.Domain.Vault;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ecommerce.Infrastructure.Vault;

/// <summary>
/// Cofre do tenant do escopo (RF06), com criptografia envelope: segredos cifrados pela chave de dados do tenant (DEK),
/// que fica no banco cifrada pela chave mestra (KEK) do ambiente. O RLS garante que só a DEK do tenant é lida.
/// </summary>
public sealed class SecretVault(TenantDbContext db, IOptions<VaultOptions> options, TimeProvider clock) : ISecretVault
{
    private const string DataKeyVersion = "1";

    public async Task<SecretMetadata> SetAsync(SecretKind kind, string name, ReadOnlyMemory<byte> value, DateTimeOffset? expiresAt = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (value.IsEmpty) throw new ArgumentException("Segredo vazio.", nameof(value));

        var tenantId = db.RequireTenantId();
        var dataKey = await GetOrCreateDataKeyAsync(tenantId, ct);
        var ciphertext = EnvelopeCrypto.Encrypt(dataKey, value.Span, SecretContext(tenantId, kind, name));
        var hint = HintFor(kind, value.Span);
        var now = clock.GetUtcNow();

        var secret = await db.TenantSecrets.SingleOrDefaultAsync(s => s.Kind == kind && s.Name == name, ct);
        if (secret is null)
            db.TenantSecrets.Add(secret = new TenantSecret(tenantId, kind, name, ciphertext, DataKeyVersion, hint, expiresAt, now));
        else
            secret.Replace(ciphertext, DataKeyVersion, hint, expiresAt, now);

        await db.SaveChangesAsync(ct);
        return ToMetadata(secret);
    }

    public async Task<byte[]?> GetAsync(SecretKind kind, string name, CancellationToken ct = default)
    {
        var tenantId = db.RequireTenantId();
        var secret = await db.TenantSecrets.AsNoTracking().SingleOrDefaultAsync(s => s.Kind == kind && s.Name == name, ct);
        if (secret is null) return null;

        var dataKey = await GetOrCreateDataKeyAsync(tenantId, ct);
        return EnvelopeCrypto.Decrypt(dataKey, secret.Ciphertext, SecretContext(tenantId, kind, name));
    }

    public async Task<IReadOnlyList<SecretMetadata>> ListAsync(CancellationToken ct = default)
    {
        var secrets = await db.TenantSecrets.AsNoTracking().OrderBy(s => s.Kind).ThenBy(s => s.Name).ToListAsync(ct);
        return secrets.Select(ToMetadata).ToList();
    }

    public async Task<bool> DeleteAsync(SecretKind kind, string name, CancellationToken ct = default) =>
        await db.TenantSecrets.Where(s => s.Kind == kind && s.Name == name).ExecuteDeleteAsync(ct) > 0;

    /// <summary>
    /// Recifra a DEK do tenant com a chave mestra atual (RF06 CA4). Os segredos não mudam; sem downtime.
    /// Retorna true se houve troca.
    /// </summary>
    public async Task<bool> RewrapDataKeyAsync(CancellationToken ct = default)
    {
        var tenantId = db.RequireTenantId();
        var stored = await db.TenantDataKeys.SingleOrDefaultAsync(ct);
        var (currentVersion, currentKey) = options.Value.CurrentMasterKey();
        if (stored is null || stored.MasterKeyVersion == currentVersion) return false;

        var dataKey = EnvelopeCrypto.Decrypt(options.Value.MasterKey(stored.MasterKeyVersion), stored.WrappedKey, DataKeyContext(tenantId));
        stored.Rewrap(EnvelopeCrypto.Encrypt(currentKey, dataKey, DataKeyContext(tenantId)), currentVersion);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<byte[]> GetOrCreateDataKeyAsync(Guid tenantId, CancellationToken ct)
    {
        var stored = await db.TenantDataKeys.AsNoTracking().SingleOrDefaultAsync(ct);
        if (stored is not null)
            return EnvelopeCrypto.Decrypt(options.Value.MasterKey(stored.MasterKeyVersion), stored.WrappedKey, DataKeyContext(tenantId));

        var (version, masterKey) = options.Value.CurrentMasterKey();
        var dataKey = EnvelopeCrypto.NewKey();
        var wrapped = EnvelopeCrypto.Encrypt(masterKey, dataKey, DataKeyContext(tenantId));

        // Duas requisições podem criar a primeira DEK ao mesmo tempo: a chave primária decide, e quem perde relê a vencedora.
        var inserted = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO tenant_data_keys (tenant_id, version, wrapped_key, master_key_version, created_at)
            VALUES ({tenantId}, {DataKeyVersion}, {wrapped}, {version}, {clock.GetUtcNow()})
            ON CONFLICT (tenant_id) DO NOTHING
            """, ct);
        if (inserted == 1) return dataKey;

        stored = await db.TenantDataKeys.AsNoTracking().SingleAsync(ct);
        return EnvelopeCrypto.Decrypt(options.Value.MasterKey(stored.MasterKeyVersion), stored.WrappedKey, DataKeyContext(tenantId));
    }

    private static string DataKeyContext(Guid tenantId) => $"dek:{tenantId}";

    private static string SecretContext(Guid tenantId, SecretKind kind, string name) => $"secret:{tenantId}:{kind}:{name}";

    /// <summary>Últimos 4 caracteres de segredos de texto com pelo menos 12 caracteres; nada para binários e segredos curtos.</summary>
    private static string HintFor(SecretKind kind, ReadOnlySpan<byte> value)
    {
        if (kind is SecretKind.A1Certificate or SecretKind.A1CertificatePassword) return string.Empty;
        string text;
        try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(value); }
        catch (DecoderFallbackException) { return string.Empty; }
        return text.Length >= 12 ? text[^4..] : string.Empty;
    }

    private static SecretMetadata ToMetadata(TenantSecret s) => new(s.Kind, s.Name, s.Hint, s.ExpiresAt, s.UpdatedAt);
}

/// <summary>Leitura da validade do certificado A1, para gravar junto do segredo e alertar antes do vencimento (RF06 CA5).</summary>
public static class A1Certificate
{
    public static DateTimeOffset ReadExpiry(ReadOnlySpan<byte> pfx, string password)
    {
        using var certificate = X509CertificateLoader.LoadPkcs12(pfx, password);
        return new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
    }
}
