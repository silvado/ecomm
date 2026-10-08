using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Vault;

public enum SecretKind
{
    PaymentGatewayCredential,
    /// <summary>Certificado digital A1 (arquivo .pfx) para NF-e.</summary>
    A1Certificate,
    A1CertificatePassword,
    OAuthToken,
    ApiKey,
}

/// <summary>
/// Segredo de um tenant (RF06). Só o texto cifrado fica no banco; o valor nunca sai da API (CA3).
/// <see cref="Hint"/> mostra no máximo os 4 últimos caracteres, para o lojista reconhecer qual chave está cadastrada.
/// </summary>
public sealed class TenantSecret : ITenantOwned
{
    private TenantSecret() { }

    public TenantSecret(Guid tenantId, SecretKind kind, string name, byte[] ciphertext, string keyVersion, string hint, DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        Kind = kind;
        Name = name;
        CreatedAt = now;
        Replace(ciphertext, keyVersion, hint, expiresAt, now);
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public SecretKind Kind { get; private set; }

    /// <summary>Identifica o segredo dentro do tipo (ex.: "mercado-pago:access-token").</summary>
    public string Name { get; private set; } = string.Empty;

    public byte[] Ciphertext { get; private set; } = [];

    /// <summary>Versão da chave de dados do tenant usada na cifra.</summary>
    public string KeyVersion { get; private set; } = string.Empty;

    public string Hint { get; private set; } = string.Empty;
    public DateTimeOffset? ExpiresAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void Replace(byte[] ciphertext, string keyVersion, string hint, DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyVersion);
        if (hint.Length > 4) throw new ArgumentException("A dica tem no máximo 4 caracteres.", nameof(hint));
        Ciphertext = ciphertext;
        KeyVersion = keyVersion;
        Hint = hint;
        ExpiresAt = expiresAt;
        UpdatedAt = now;
    }

    public override string ToString() => $"TenantSecret({Kind}:{Name}, ***)";
}

/// <summary>
/// Chave de dados (DEK) de um tenant, cifrada pela chave mestra (KEK) — criptografia envelope (RF06 CA1).
/// Trocar a chave mestra só exige recifrar estas linhas, não os segredos (CA4).
/// </summary>
public sealed class TenantDataKey : ITenantOwned
{
    private TenantDataKey() { }

    public TenantDataKey(Guid tenantId, byte[] wrappedKey, string masterKeyVersion, DateTimeOffset now)
    {
        TenantId = tenantId;
        Version = "1";
        CreatedAt = now;
        Rewrap(wrappedKey, masterKeyVersion);
    }

    public Guid TenantId { get; private set; }
    public string Version { get; private set; } = string.Empty;
    public byte[] WrappedKey { get; private set; } = [];
    public string MasterKeyVersion { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }

    public void Rewrap(byte[] wrappedKey, string masterKeyVersion)
    {
        ArgumentNullException.ThrowIfNull(wrappedKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(masterKeyVersion);
        WrappedKey = wrappedKey;
        MasterKeyVersion = masterKeyVersion;
    }

    public override string ToString() => $"TenantDataKey({TenantId}, master {MasterKeyVersion}, ***)";
}
