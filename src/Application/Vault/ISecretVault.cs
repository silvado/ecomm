using Ecommerce.Domain.Vault;

namespace Ecommerce.Application.Vault;

/// <summary>O que o painel pode ver de um segredo (RF06 CA3): nunca o valor.</summary>
public sealed record SecretMetadata(SecretKind Kind, string Name, string Hint, DateTimeOffset? ExpiresAt, DateTimeOffset UpdatedAt);

/// <summary>
/// Cofre de segredos do tenant do escopo (RF06). Valores só circulam dentro do servidor, para os adapters
/// (gateway, NF-e, canais); nenhuma API pública devolve <see cref="GetAsync"/>.
/// </summary>
public interface ISecretVault
{
    /// <summary>Cria ou substitui. Para certificado A1, informe a validade (ver A1Certificate.ReadExpiry na Infrastructure).</summary>
    Task<SecretMetadata> SetAsync(SecretKind kind, string name, ReadOnlyMemory<byte> value, DateTimeOffset? expiresAt = null, CancellationToken ct = default);

    Task<byte[]?> GetAsync(SecretKind kind, string name, CancellationToken ct = default);

    Task<IReadOnlyList<SecretMetadata>> ListAsync(CancellationToken ct = default);

    Task<bool> DeleteAsync(SecretKind kind, string name, CancellationToken ct = default);
}
