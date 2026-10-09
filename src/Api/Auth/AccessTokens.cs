using System.Security.Claims;
using Ecommerce.Application.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ecommerce.Api.Auth;

/// <summary>
/// Chave do JWT do painel, só por variável de ambiente (RNF05): <c>Auth__SigningKey=&lt;base64 de 32+ bytes&gt;</c>.
/// HMAC basta: quem emite e quem valida é a própria API.
/// </summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";
    public const string Issuer = "ecommerce-api";
    public const string Audience = "ecommerce-painel";

    /// <summary>Curto: o token vive só na memória do painel e a renovação usa o cookie.</summary>
    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);

    public string SigningKey { get; set; } = string.Empty;

    public SymmetricSecurityKey Key()
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(SigningKey); }
        catch (FormatException) { throw new InvalidOperationException("Auth:SigningKey precisa estar em base64."); }
        if (bytes.Length < 32) throw new InvalidOperationException("Auth:SigningKey precisa ter pelo menos 32 bytes.");
        return new SymmetricSecurityKey(bytes);
    }
}

public static class PanelClaims
{
    public const string UserId = JwtRegisteredClaimNames.Sub;
    public const string TenantId = "tid";
    /// <summary>Senha provisória: só a troca de senha é liberada.</summary>
    public const string MustChangePassword = "pwd_change";
}

public sealed class AccessTokens(IOptions<AuthOptions> options, TimeProvider clock)
{
    private readonly JsonWebTokenHandler _handler = new();

    public (string Token, DateTimeOffset ExpiresAt) Issue(Session session)
    {
        var now = clock.GetUtcNow();
        var expires = now + AuthOptions.AccessTokenLifetime;
        var claims = new Dictionary<string, object>
        {
            [PanelClaims.UserId] = session.UserId.ToString(),
            [JwtRegisteredClaimNames.Email] = session.Email,
            [JwtRegisteredClaimNames.Jti] = Guid.CreateVersion7().ToString(),
        };
        if (session.Tenant is not null) claims[PanelClaims.TenantId] = session.Tenant.TenantId.ToString();
        if (session.MustChangePassword) claims[PanelClaims.MustChangePassword] = true;

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = AuthOptions.Issuer,
            Audience = AuthOptions.Audience,
            Claims = claims,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(options.Value.Key(), SecurityAlgorithms.HmacSha256),
        });
        return (token, expires);
    }

    public static TokenValidationParameters ValidationParameters(AuthOptions options) => new()
    {
        ValidIssuer = AuthOptions.Issuer,
        ValidAudience = AuthOptions.Audience,
        IssuerSigningKey = options.Key(),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = PanelClaims.UserId,
    };
}

public static class ClaimsPrincipalExtensions
{
    public static Guid? PanelUserId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(PanelClaims.UserId), out var id) ? id : null;

    public static Guid? PanelTenantId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(PanelClaims.TenantId), out var id) ? id : null;

    public static bool MustChangePassword(this ClaimsPrincipal user) => user.HasClaim(c => c.Type == PanelClaims.MustChangePassword);
}
