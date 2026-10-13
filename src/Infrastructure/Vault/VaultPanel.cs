using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Ecommerce.Application.Vault;
using Ecommerce.Domain.Vault;
using Ecommerce.Infrastructure.Fiscal;
using Ecommerce.Infrastructure.Persistence;

namespace Ecommerce.Infrastructure.Vault;

/// <summary>
/// Cofre no painel (RF06, só o Dono): metadados dos segredos e o certificado A1 da NF-e (arquivo .pfx e senha cifrados no
/// cofre; validade guardada junto para o aviso de vencimento — CA5). Nenhum valor volta ao navegador.
/// </summary>
public sealed partial class VaultPanel(ISecretVault vault, TenantDbContext db, TenantIssuer issuer, TimeProvider clock) : IVaultPanel
{
    public const string CertificateName = "a1:certificado";
    public const string PasswordName = "a1:senha";
    public const int MaxPfxBytes = 100 * 1024;

    // ICP-Brasil: o titular do e-CNPJ vem como "RAZAO SOCIAL:CNPJ" (o CNPJ pode ser alfanumérico).
    [GeneratedRegex(":([0-9A-Z]{12}[0-9]{2})$")]
    private static partial Regex HolderCnpj();

    public Task<IReadOnlyList<SecretMetadata>> SecretsAsync(CancellationToken ct = default) => vault.ListAsync(ct);

    public async Task<CertificateView?> CertificateAsync(CancellationToken ct = default)
    {
        var pfx = await vault.GetAsync(SecretKind.A1Certificate, CertificateName, ct);
        var password = await vault.GetAsync(SecretKind.A1CertificatePassword, PasswordName, ct);
        if (pfx is null || password is null) return null;
        var updatedAt = (await vault.ListAsync(ct)).First(s => s.Kind == SecretKind.A1Certificate && s.Name == CertificateName).UpdatedAt;
        using var certificate = Load(pfx, Encoding.UTF8.GetString(password));
        return View(certificate, updatedAt);
    }

    public async Task<CertificateView> SaveCertificateAsync(byte[] pfx, string password, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pfx);
        if (pfx.Length == 0) throw new ArgumentException("Envie o arquivo do certificado (.pfx ou .p12).", nameof(pfx));
        if (pfx.Length > MaxPfxBytes) throw new ArgumentException("Arquivo grande demais para um certificado A1.", nameof(pfx));
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("Informe a senha do certificado.", nameof(password));

        using var certificate = Load(pfx, password);
        if (!certificate.HasPrivateKey) throw new ArgumentException("O arquivo não traz a chave privada: exporte o certificado A1 completo (.pfx).", nameof(pfx));
        var now = clock.GetUtcNow();
        var expiresAt = new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        if (expiresAt <= now) throw new ArgumentException($"Este certificado venceu em {expiresAt:dd/MM/yyyy}. Envie o certificado renovado.", nameof(pfx));
        var holderCnpj = Cnpj(certificate);
        var storeCnpj = await issuer.CnpjAsync(db.RequireTenantId(), ct);
        if (holderCnpj is not null && holderCnpj != storeCnpj)
            throw new ArgumentException($"Este certificado é do CNPJ {holderCnpj}, não do CNPJ da loja ({storeCnpj}).", nameof(pfx));

        // Arquivo e senha juntos ou nada.
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await vault.SetAsync(SecretKind.A1Certificate, CertificateName, pfx, expiresAt, ct);
            await vault.SetAsync(SecretKind.A1CertificatePassword, PasswordName, Encoding.UTF8.GetBytes(password), expiresAt, ct);
            await transaction.CommitAsync(ct);
        }
        return View(certificate, now);
    }

    public async Task<bool> RemoveCertificateAsync(CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var removed = await vault.DeleteAsync(SecretKind.A1Certificate, CertificateName, ct);
        await vault.DeleteAsync(SecretKind.A1CertificatePassword, PasswordName, ct);
        await transaction.CommitAsync(ct);
        return removed;
    }

    public async Task<CertificateAlert?> AlertAsync(CancellationToken ct = default)
    {
        var expiresAt = (await vault.ListAsync(ct)).FirstOrDefault(s => s.Kind == SecretKind.A1Certificate && s.Name == CertificateName)?.ExpiresAt;
        return expiresAt is { } at ? CertificateAlerts.For(at, clock.GetUtcNow()) : null;
    }

    private CertificateView View(X509Certificate2 certificate, DateTimeOffset updatedAt)
    {
        var expiresAt = new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        return new CertificateView(Holder(certificate), Cnpj(certificate), new DateTimeOffset(certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero),
            expiresAt, CertificateAlerts.DaysLeft(expiresAt, clock.GetUtcNow()), updatedAt);
    }

    /// <summary>Senha errada e arquivo que não é PKCS#12 dão a mesma mensagem, sem detalhes do erro (nem a senha).</summary>
    private static X509Certificate2 Load(byte[] pfx, string password)
    {
        try
        {
            return X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException)
        {
            throw new ArgumentException("Não foi possível abrir o certificado: confira o arquivo (.pfx) e a senha.");
        }
    }

    private static string Holder(X509Certificate2 certificate)
    {
        var name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        var match = HolderCnpj().Match(name);
        return match.Success ? name[..match.Index] : name;
    }

    private static string? Cnpj(X509Certificate2 certificate)
    {
        var match = HolderCnpj().Match(certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
        return match.Success ? match.Groups[1].Value : null;
    }
}
