using System.Security.Claims;
using Ecommerce.Api.Auth;
using Ecommerce.Application.Platform;
using Ecommerce.Domain.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ecommerce.Api.Platform;

/// <summary>Etapas do login do superadmin, gravadas no token (ADR-0007).</summary>
public static class PlatformStages
{
    /// <summary>Senha ok, falta o código do app (ou de recuperação).</summary>
    public const string Code = "codigo";
    /// <summary>Primeiro acesso: trocar a senha provisória e cadastrar o MFA.</summary>
    public const string Setup = "cadastro";
    /// <summary>Senha + MFA: libera o painel da plataforma.</summary>
    public const string Full = "completo";
}

/// <summary>
/// Token do painel da plataforma: mesma chave do painel de loja, mas outra audiência — um não abre as rotas do outro.
/// </summary>
public sealed class PlatformTokens(IOptions<AuthOptions> options, TimeProvider clock)
{
    public const string Scheme = "Plataforma";
    public const string Audience = "ecommerce-plataforma";
    public const string Policy = "plataforma";
    public const string StepPolicy = "plataforma-etapa";
    public const string StageClaim = "stage";
    public const string SessionClaim = "sid";

    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SetupLifetime = TimeSpan.FromMinutes(15);
    private readonly JsonWebTokenHandler _handler = new();

    public (string Token, DateTimeOffset ExpiresAt) Issue(PlatformAdminInfo admin, string stage, Guid? sessionId = null, DateTimeOffset? sessionEnd = null)
    {
        var now = clock.GetUtcNow();
        var expires = now + stage switch
        {
            PlatformStages.Code => CodeLifetime,
            PlatformStages.Setup => SetupLifetime,
            _ => AuthOptions.AccessTokenLifetime,
        };
        if (sessionEnd < expires) expires = sessionEnd.Value;

        var claims = new Dictionary<string, object>
        {
            [JwtRegisteredClaimNames.Sub] = admin.Id.ToString(),
            [JwtRegisteredClaimNames.Email] = admin.Email,
            [JwtRegisteredClaimNames.Jti] = Guid.CreateVersion7().ToString(),
            [StageClaim] = stage,
        };
        if (sessionId is not null) claims[SessionClaim] = sessionId.Value.ToString();

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = AuthOptions.Issuer,
            Audience = Audience,
            Claims = claims,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(options.Value.Key(), SecurityAlgorithms.HmacSha256),
        });
        return (token, expires);
    }

    public static TokenValidationParameters ValidationParameters(AuthOptions options)
    {
        var parameters = AccessTokens.ValidationParameters(options);
        parameters.ValidAudience = Audience;
        return parameters;
    }

    public static void AddPlatformAuthentication(IServiceCollection services, AuthOptions authOptions)
    {
        services.AddAuthentication().AddJwtBearer(Scheme, o =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = ValidationParameters(authOptions);
        });
        services.AddAuthorizationBuilder()
            .AddPolicy(StepPolicy, p => p.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser())
            .AddPolicy(Policy, p => p.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser().RequireClaim(StageClaim, PlatformStages.Full));
        services.AddSingleton<PlatformTokens>();
    }
}

public static class PlatformClaims
{
    public static Guid? PlatformAdminId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id) ? id : null;

    public static Guid? PlatformSessionId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(PlatformTokens.SessionClaim), out var id) ? id : null;

    public static string? PlatformStage(this ClaimsPrincipal user) => user.FindFirstValue(PlatformTokens.StageClaim);
}

public sealed record PlatformLoginRequest(string Email, string Password);
public sealed record PlatformCodeRequest(string Code);
public sealed record PlatformAdminResponse(string Email, bool MustChangePassword, bool HasMfa);
public sealed record PlatformStepResponse(string Stage, string AccessToken, DateTimeOffset ExpiresAt, PlatformAdminResponse Admin, IReadOnlyList<string>? RecoveryCodes = null);

/// <summary>
/// Login do superadmin (ADR-0007): senha → código (ou, na primeira vez, troca de senha e cadastro do MFA) → sessão.
/// O cookie de sessão só existe depois do MFA e vale só para estas rotas.
/// </summary>
public static class PlatformAuthEndpoints
{
    public const string SessionCookie = "platform_session";
    private const string CookiePath = "/api/plataforma/auth";

    public static void MapPlatformAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup(CookiePath).AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.RequestServices.GetRequiredService<PlatformActor>().Ip = context.HttpContext.Connection.RemoteIpAddress?.ToString();
            return await next(context);
        });

        auth.MapPost("/entrar", async Task<Results<Ok<PlatformStepResponse>, ProblemHttpResult>> (
            PlatformLoginRequest request, IPlatformAdminAuth service, PlatformTokens tokens, CancellationToken ct) =>
        {
            var result = await service.LoginAsync(request.Email ?? string.Empty, request.Password ?? string.Empty, ct);
            if (result.Value is not { } admin) return ProblemFor(result.Error!.Value);
            return Step(tokens, admin, admin.HasMfa && !admin.MustChangePassword ? PlatformStages.Code : PlatformStages.Setup);
        }).RequireRateLimiting(AuthEndpoints.LoginRateLimit);

        var step = auth.MapGroup("").RequireAuthorization(PlatformTokens.StepPolicy);

        step.MapPost("/codigo", async Task<Results<Ok<PlatformStepResponse>, ProblemHttpResult>> (
            PlatformCodeRequest request, IPlatformAdminAuth service, PlatformTokens tokens, HttpContext http, CancellationToken ct) =>
        {
            if (Stage(http, PlatformStages.Code) is not { } adminId) return WrongStep();
            var result = await service.VerifyCodeAsync(adminId, request.Code ?? string.Empty, ct);
            return result.Value is { } session ? Open(tokens, http, session) : ProblemFor(result.Error!.Value);
        }).RequireRateLimiting(AuthEndpoints.LoginRateLimit);

        step.MapPut("/senha", async Task<Results<Ok<PlatformStepResponse>, ProblemHttpResult>> (
            ChangePasswordRequest request, IPlatformAdminAuth service, PlatformTokens tokens, HttpContext http, CancellationToken ct) =>
        {
            if (Stage(http, PlatformStages.Setup) is not { } adminId) return WrongStep();
            var result = await service.ChangePasswordAsync(adminId, request.CurrentPassword ?? string.Empty, request.NewPassword ?? string.Empty, ct);
            return result.Value is { } admin ? Step(tokens, admin, PlatformStages.Setup) : ProblemFor(result.Error!.Value);
        }).RequireRateLimiting(AuthEndpoints.LoginRateLimit);

        step.MapPost("/mfa", async Task<Results<Ok<MfaEnrollment>, ProblemHttpResult>> (IPlatformAdminAuth service, HttpContext http, CancellationToken ct) =>
        {
            if (Stage(http, PlatformStages.Setup) is not { } adminId) return WrongStep();
            var result = await service.StartEnrollmentAsync(adminId, ct);
            http.Response.Headers.CacheControl = "no-store";
            return result.Value is { } enrollment ? TypedResults.Ok(enrollment) : ProblemFor(result.Error!.Value);
        });

        step.MapPost("/mfa/confirmar", async Task<Results<Ok<PlatformStepResponse>, ProblemHttpResult>> (
            PlatformCodeRequest request, IPlatformAdminAuth service, PlatformTokens tokens, HttpContext http, CancellationToken ct) =>
        {
            if (Stage(http, PlatformStages.Setup) is not { } adminId) return WrongStep();
            var result = await service.ConfirmEnrollmentAsync(adminId, request.Code ?? string.Empty, ct);
            return result.Value is { } confirmed ? Open(tokens, http, confirmed.Session, confirmed.RecoveryCodes) : ProblemFor(result.Error!.Value);
        }).RequireRateLimiting(AuthEndpoints.LoginRateLimit);

        auth.MapPost("/renovar", async Task<Results<Ok<PlatformStepResponse>, ProblemHttpResult>> (
            IPlatformAdminAuth service, PlatformTokens tokens, HttpContext http, CancellationToken ct) =>
        {
            var result = await service.ResumeAsync(http.Request.Cookies[SessionCookie] ?? string.Empty, ct);
            if (result.Value is not { } session) return ProblemFor(result.Error!.Value);
            var (token, expiresAt) = tokens.Issue(session.Admin, PlatformStages.Full, session.SessionId, session.ExpiresAt);
            http.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(new PlatformStepResponse(PlatformStages.Full, token, expiresAt, Response(session.Admin)));
        });

        auth.MapPost("/sair", async (IPlatformAdminAuth service, HttpContext http, CancellationToken ct) =>
        {
            await service.LogoutAsync(http.Request.Cookies[SessionCookie] ?? string.Empty, ct);
            http.Response.Cookies.Delete(SessionCookie, CookieOptions(DateTimeOffset.UnixEpoch));
            return TypedResults.NoContent();
        });
    }

    private static Guid? Stage(HttpContext http, string expected) =>
        http.User.PlatformStage() == expected ? http.User.PlatformAdminId() : null;

    private static Ok<PlatformStepResponse> Step(PlatformTokens tokens, PlatformAdminInfo admin, string stage)
    {
        var (token, expiresAt) = tokens.Issue(admin, stage);
        return TypedResults.Ok(new PlatformStepResponse(stage, token, expiresAt, Response(admin)));
    }

    private static Ok<PlatformStepResponse> Open(PlatformTokens tokens, HttpContext http, PlatformSession session, IReadOnlyList<string>? recoveryCodes = null)
    {
        var (token, expiresAt) = tokens.Issue(session.Admin, PlatformStages.Full, session.SessionId, session.ExpiresAt);
        http.Response.Cookies.Append(SessionCookie, session.RefreshToken, CookieOptions(session.ExpiresAt));
        http.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(new PlatformStepResponse(PlatformStages.Full, token, expiresAt, Response(session.Admin), recoveryCodes));
    }

    private static PlatformAdminResponse Response(PlatformAdminInfo admin) => new(admin.Email, admin.MustChangePassword, admin.HasMfa);

    private static CookieOptions CookieOptions(DateTimeOffset expires) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = CookiePath,
        Expires = expires,
        IsEssential = true,
    };

    private static ProblemHttpResult WrongStep() =>
        AuthEndpoints.Problem(StatusCodes.Status403Forbidden, "Etapa do acesso inválida. Entre novamente.");

    private static ProblemHttpResult ProblemFor(PlatformAuthError error) => error switch
    {
        PlatformAuthError.InvalidCredentials => AuthEndpoints.Problem(StatusCodes.Status401Unauthorized,
            "E-mail ou senha inválidos, ou acesso bloqueado temporariamente por excesso de tentativas."),
        PlatformAuthError.InvalidCode => AuthEndpoints.Problem(StatusCodes.Status401Unauthorized,
            "Código inválido ou já usado, ou acesso bloqueado temporariamente por excesso de tentativas."),
        PlatformAuthError.InvalidStep => WrongStep(),
        PlatformAuthError.InvalidSession => AuthEndpoints.Problem(StatusCodes.Status401Unauthorized, "Sessão expirada. Entre novamente."),
        PlatformAuthError.InvalidNewPassword => AuthEndpoints.Problem(StatusCodes.Status400BadRequest,
            $"A nova senha precisa ter de {PasswordPolicy.MinLength} a {PasswordPolicy.MaxLength} caracteres e ser diferente da atual."),
        _ => throw new ArgumentOutOfRangeException(nameof(error)),
    };
}
