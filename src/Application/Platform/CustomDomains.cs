using Ecommerce.Domain.Platform;

namespace Ecommerce.Application.Platform;

/// <summary>
/// Consulta de DNS (RF03 CA2) atrás de interface: DnsClient em produção, simulada em dev e testes. Só lê CNAME, A e o TXT
/// de verificação — nunca MX nem TXT de e-mail (CA4). Nome inexistente devolve lista vazia.
/// </summary>
public interface IDnsResolver
{
    Task<IReadOnlyList<string>> CnamesAsync(string host, CancellationToken ct = default);

    Task<IReadOnlyList<string>> AddressesAsync(string host, CancellationToken ct = default);

    Task<IReadOnlyList<string>> TextsAsync(string host, CancellationToken ct = default);
}

/// <summary>Registro que a loja precisa criar no DNS (RF03 CA1).</summary>
/// <param name="Alternative">Verdadeiro quando é uma opção a outra linha (ex.: A no lugar de ALIAS).</param>
public sealed record DnsInstruction(string Type, string Name, string Value, string Note, bool Alternative = false);

public sealed record CustomDomainView(Guid Id, string Host, DomainKind Kind, DomainVerificationStatus Status, bool IsPrimary,
    DateTimeOffset? RequestedAt, DateTimeOffset? LastCheckedAt, string? LastCheckMessage, DateTimeOffset? VerifiedAt, IReadOnlyList<DnsInstruction> Records);

/// <param name="Allowed">Falso quando o plano da loja não inclui domínio próprio (RF37).</param>
public sealed record CustomDomainsView(bool Allowed, string PlatformHost, IReadOnlyList<CustomDomainView> Domains);

/// <summary>Domínio próprio no painel (RF03, só o Dono).</summary>
public interface ICustomDomains
{
    Task<CustomDomainsView> ListAsync(CancellationToken ct = default);

    /// <exception cref="ArgumentException">Domínio mal escrito ou da plataforma.</exception>
    /// <exception cref="InvalidOperationException">Fora do plano, já cadastrado aqui ou em outra loja.</exception>
    Task<CustomDomainsView> AddAsync(string? host, CancellationToken ct = default);

    Task<CustomDomainsView?> RemoveAsync(Guid domainId, CancellationToken ct = default);

    /// <summary>Confere agora (e reabre a janela de 72 h se tinha falhado).</summary>
    Task<CustomDomainsView?> VerifyNowAsync(Guid domainId, CancellationToken ct = default);
}
