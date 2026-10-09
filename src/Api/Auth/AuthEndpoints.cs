using Ecommerce.Application.Identity;
using Ecommerce.Domain.Identity;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ecommerce.Api.Auth;

public sealed record LoginRequest(string Email, string Password);
public sealed record SelectTenantRequest(Guid TenantId);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record TenantAccessResponse(Guid Id, string Slug, string Name, TenantRole Role, IReadOnlyCollection<Permission> Permissions)
{
    public static TenantAccessResponse From(TenantAccess access) =>
        new(access.TenantId, access.Slug, access.Name, access.Role, access.Permissions.Order().ToList());
}

public sealed record SessionResponse(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    Guid UserId,
    string Email,
    bool MustChangePassword,
    TenantAccessResponse? Tenant,
    IReadOnlyList<TenantAccessResponse> Tenants);

/// <summary>
/// Login e sessão do painel (RF07). O token de acesso vai no corpo (o painel guarda só em memória);
/// o de renovação, só no cookie HttpOnly — o JavaScript nunca o vê.
/// </summary>
public static class AuthEndpoints
{
    public const string RefreshCookie = "refresh_token";
    public const string LoginRateLimit = "login";

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth");

        auth.MapPost("/login", async (LoginRequest request, IAuthService service, AccessTokens tokens, HttpContext http, CancellationToken ct) =>
            Respond(await service.LoginAsync(request.Email ?? string.Empty, request.Password ?? string.Empty, ct), tokens, http))
            .RequireRateLimiting(LoginRateLimit);

        auth.MapPost("/refresh", async (IAuthService service, AccessTokens tokens, HttpContext http, CancellationToken ct) =>
            Respond(await service.RefreshAsync(http.Request.Cookies[RefreshCookie] ?? string.Empty, ct), tokens, http));

        auth.MapPost("/loja", async (SelectTenantRequest request, IAuthService service, AccessTokens tokens, HttpContext http, CancellationToken ct) =>
            Respond(await service.SelectTenantAsync(http.Request.Cookies[RefreshCookie] ?? string.Empty, request.TenantId, ct), tokens, http));

        auth.MapPost("/logout", async (IAuthService service, HttpContext http, CancellationToken ct) =>
        {
            await service.LogoutAsync(http.Request.Cookies[RefreshCookie] ?? string.Empty, ct);
            http.Response.Cookies.Delete(RefreshCookie, CookieOptions(DateTimeOffset.UnixEpoch));
            return TypedResults.NoContent();
        });

        auth.MapPut("/senha", async Task<Results<Ok<SessionResponse>, ProblemHttpResult>> (ChangePasswordRequest request, IAuthService service, AccessTokens tokens, HttpContext http, CancellationToken ct) =>
        {
            var userId = http.User.PanelUserId();
            if (userId is null) return Problem(StatusCodes.Status401Unauthorized, "Sessão inválida.");
            var result = await service.ChangePasswordAsync(
                userId.Value, http.User.PanelTenantId(), request.CurrentPassword ?? string.Empty, request.NewPassword ?? string.Empty, ct);
            return Respond(result, tokens, http);
        }).RequireAuthorization();
    }

    private static Results<Ok<SessionResponse>, ProblemHttpResult> Respond(AuthResult result, AccessTokens tokens, HttpContext http)
    {
        if (result.Session is not { } session) return ProblemFor(result.Error!.Value);

        var (accessToken, expiresAt) = tokens.Issue(session);
        http.Response.Cookies.Append(RefreshCookie, session.RefreshToken, CookieOptions(DateTimeOffset.UtcNow + RefreshToken.Lifetime));
        http.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(new SessionResponse(
            accessToken, expiresAt, session.UserId, session.Email, session.MustChangePassword,
            session.Tenant is null ? null : TenantAccessResponse.From(session.Tenant),
            session.Tenants.Select(TenantAccessResponse.From).ToList()));
    }

    private static CookieOptions CookieOptions(DateTimeOffset expires) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/api/auth",
        Expires = expires,
        IsEssential = true,
    };

    private static ProblemHttpResult ProblemFor(AuthError error) => error switch
    {
        AuthError.InvalidCredentials => Problem(StatusCodes.Status401Unauthorized,
            "E-mail ou senha inválidos, ou acesso bloqueado temporariamente por excesso de tentativas."),
        AuthError.InvalidSession => Problem(StatusCodes.Status401Unauthorized, "Sessão expirada. Entre novamente."),
        AuthError.NoStore => Problem(StatusCodes.Status403Forbidden, "Seu usuário não tem acesso a nenhuma loja."),
        AuthError.StoreNotAvailable => Problem(StatusCodes.Status403Forbidden, "Você não tem acesso a esta loja."),
        AuthError.ConcurrentRefresh => Problem(StatusCodes.Status409Conflict, "Sessão renovada em outra aba. Tente novamente."),
        AuthError.InvalidNewPassword => Problem(StatusCodes.Status400BadRequest,
            $"A nova senha precisa ter de {PasswordPolicy.MinLength} a {PasswordPolicy.MaxLength} caracteres e ser diferente da atual."),
        _ => throw new ArgumentOutOfRangeException(nameof(error)),
    };

    internal static ProblemHttpResult Problem(int status, string title, string? type = null) =>
        TypedResults.Problem(statusCode: status, title: title, type: type);
}
