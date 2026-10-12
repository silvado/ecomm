using System.Security.Cryptography;
using System.Text;
using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Customers;

/// <summary>
/// Link de acesso à área do cliente (RF16): de uso único, válido por 15 minutos, enviado ao e-mail do comprador. Só o hash
/// do token é guardado; o token vai no e-mail e volta pelo navegador.
/// </summary>
public sealed class CustomerAccessLink : ITenantOwned
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private CustomerAccessLink() { }

    public CustomerAccessLink(Guid tenantId, string email, byte[] tokenHash, DateTimeOffset now)
    {
        if (tokenHash.Length != 32) throw new ArgumentException("Hash do token inválido.", nameof(tokenHash));
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        Email = CustomerEmail.Normalize(email);
        TokenHash = tokenHash;
        CreatedAt = now;
        ExpiresAt = now + Lifetime;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public byte[] TokenHash { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? UsedAt { get; private set; }
}

/// <summary>Sessão do comprador na área do cliente de uma loja (RF16): 30 dias, só no navegador (cookie HttpOnly).</summary>
public sealed class CustomerSession : ITenantOwned
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    private CustomerSession() { }

    public CustomerSession(Guid tenantId, string email, byte[] tokenHash, DateTimeOffset now)
    {
        if (tokenHash.Length != 32) throw new ArgumentException("Hash do token inválido.", nameof(tokenHash));
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        Email = CustomerEmail.Normalize(email);
        TokenHash = tokenHash;
        CreatedAt = now;
        ExpiresAt = now + Lifetime;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public byte[] TokenHash { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }

    public bool IsActive(DateTimeOffset now) => EndedAt is null && now < ExpiresAt;

    public void End(DateTimeOffset now) => EndedAt ??= now;
}

/// <summary>E-mail do comprador como o pedido o guarda (minúsculo, sem espaços) e tokens opacos do acesso.</summary>
public static class CustomerEmail
{
    public static string Normalize(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>32 bytes aleatórios em base64url (43 caracteres).</summary>
    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>SHA-256 do token, se ele tem o formato esperado; nulo se não tem (nem consulta o banco).</summary>
    public static byte[]? HashOrNull(string? token) =>
        token is { Length: 43 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? SHA256.HashData(Encoding.ASCII.GetBytes(token)) : null;
}
