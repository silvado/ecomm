using Ecommerce.Api.Auth;
using Ecommerce.Application.Customers;
using Ecommerce.Application.Orders;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ecommerce.Api.Customers;

public sealed record CustomerLinkRequest(string? Email);
public sealed record CustomerSessionRequest(string? Token);
public sealed record CustomerSessionView(string Email);
public sealed record CustomerOrdersView(string Email, IReadOnlyList<CustomerOrderSummary> Orders);

/// <summary>
/// Área do cliente na vitrine (RF16): o comprador pede um link no e-mail, o link abre uma sessão de 30 dias num cookie
/// HttpOnly restrito a estas rotas, e ele vê só os pedidos do próprio e-mail nesta loja.
/// </summary>
public static class CustomerEndpoints
{
    public const string CookieName = "cliente_sessao";
    public const string CookiePath = "/api/loja/cliente";
    public const string RateLimit = "cliente-acesso";

    public static void MapCustomerArea(this RouteGroupBuilder store)
    {
        var customer = store.MapGroup("/cliente");

        // Sempre 202, tenha o e-mail pedidos ou não (não revela quem comprou na loja).
        customer.MapPost("/acesso", async (CustomerLinkRequest request, HttpRequest http, ICustomerArea area, CancellationToken ct) =>
        {
            await area.RequestLinkAsync(request.Email, $"{http.Scheme}://{http.Host}", ct);
            return TypedResults.Accepted((string?)null);
        }).RequireRateLimiting(RateLimit);

        customer.MapPost("/sessao", async Task<Results<Ok<CustomerSessionView>, ProblemHttpResult>> (
            CustomerSessionRequest request, HttpContext http, ICustomerArea area, CancellationToken ct) =>
        {
            var result = await area.StartSessionAsync(request.Token, ct);
            if (result.Outcome != StartSessionOutcome.Started)
                return AuthEndpoints.Problem(StatusCodes.Status400BadRequest, "Este link venceu ou já foi usado. Peça um novo link.");
            http.Response.Cookies.Append(CookieName, result.SessionToken!, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Path = CookiePath,
                Expires = result.ExpiresAt,
                IsEssential = true,
            });
            http.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(new CustomerSessionView(result.Email!));
        }).RequireRateLimiting(RateLimit);

        customer.MapGet("/pedidos", async Task<Results<Ok<CustomerOrdersView>, UnauthorizedHttpResult>> (
            HttpContext http, ICustomerArea area, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (await SessionEmailAsync(http, area, ct) is not { } email) return TypedResults.Unauthorized();
            return TypedResults.Ok(new CustomerOrdersView(email, await area.OrdersAsync(email, ct)));
        });

        customer.MapGet("/pedidos/{numero:long}", async Task<Results<Ok<StoreOrderView>, UnauthorizedHttpResult, NotFound>> (
            long numero, HttpContext http, ICustomerArea area, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (await SessionEmailAsync(http, area, ct) is not { } email) return TypedResults.Unauthorized();
            return await area.OrderAsync(email, numero, ct) is { } order ? TypedResults.Ok(order) : TypedResults.NotFound();
        });

        customer.MapPost("/sair", async (HttpContext http, ICustomerArea area, CancellationToken ct) =>
        {
            await area.EndSessionAsync(http.Request.Cookies[CookieName], ct);
            http.Response.Cookies.Delete(CookieName, new CookieOptions { Path = CookiePath, Secure = true, HttpOnly = true, SameSite = SameSiteMode.Lax });
            return TypedResults.NoContent();
        });
    }

    private static Task<string?> SessionEmailAsync(HttpContext http, ICustomerArea area, CancellationToken ct) =>
        area.SessionEmailAsync(http.Request.Cookies[CookieName], ct);
}
