using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ecommerce.Api.Auth;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Platform;
using Microsoft.AspNetCore.Mvc;

namespace Ecommerce.Integration.Tests.Api;

internal sealed record TenantDto(Guid Id, string Slug, string Name, string Role, List<string> Permissions);
internal sealed record SessionDto(string AccessToken, Guid UserId, string Email, bool MustChangePassword, TenantDto? Tenant, List<TenantDto> Tenants);
internal sealed record MeDto(Guid UserId, TenantDto Tenant);
internal sealed record StoreUserDto(Guid UserId, string Email, string Role, bool LockedOut, bool MustChangePassword);
internal sealed record Login(HttpResponseMessage Response, SessionDto? Session, string? Cookie);

/// <summary>Chamadas do painel usadas pelos testes de API (login, cookie de renovação, token no cabeçalho).</summary>
internal static class PanelHttp
{
    public const string Password = "senha-do-dono-123";

    /// <summary>Cria um usuário com o papel na loja e devolve o token de acesso dele.</summary>
    public static async Task<string> SessionForAsync(this ApiFactory api, Tenant tenant, TenantRole role = TenantRole.Owner)
    {
        var user = await api.CreateUserAsync(tenant.Id, role, Password);
        return (await LoginAsync(api.PanelClient(), user.Email, Password)).Session!.AccessToken;
    }

    public static async Task<Login> LoginAsync(HttpClient client, string email, string password) =>
        await ReadSessionAsync(await client.PostAsJsonAsync("/api/auth/login", new { email, password }));

    public static async Task<Login> RefreshAsync(HttpClient client, string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        request.Headers.Add("Cookie", $"{AuthEndpoints.RefreshCookie}={cookie}");
        return await ReadSessionAsync(await client.SendAsync(request));
    }

    public static async Task<Login> SelectTenantAsync(HttpClient client, string cookie, Guid tenantId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/loja") { Content = JsonContent.Create(new { tenantId }) };
        request.Headers.Add("Cookie", $"{AuthEndpoints.RefreshCookie}={cookie}");
        return await ReadSessionAsync(await client.SendAsync(request));
    }

    public static Task<HttpResponseMessage> GetAsync(HttpClient client, string token, string path) =>
        SendAsync(client, token, HttpMethod.Get, path);

    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    public static async Task<string> TitleAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Title!;

    private static async Task<Login> ReadSessionAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) return new Login(response, null, null);
        var cookie = response.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith(AuthEndpoints.RefreshCookie + "=", StringComparison.Ordinal))
            .Split(';')[0][(AuthEndpoints.RefreshCookie.Length + 1)..];
        return new Login(response, await response.Content.ReadFromJsonAsync<SessionDto>(), cookie);
    }
}
