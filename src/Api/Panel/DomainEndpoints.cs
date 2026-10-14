using Ecommerce.Api.Auth;
using Ecommerce.Application.Platform;
using Ecommerce.Domain.Identity;
using Ecommerce.Infrastructure.Platform;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ecommerce.Api.Panel;

public sealed record AddDomainRequest(string? Dominio);
public sealed record FakeDnsRequest(string? Host, string? Tipo, IReadOnlyList<string>? Valores);

/// <summary>Domínio próprio da loja (RF03), em Dados da loja, só para o Dono e nos planos com o recurso (RF37).</summary>
public static class DomainEndpoints
{
    public static void MapDomainEndpoints(this RouteGroupBuilder panel)
    {
        var domains = panel.MapGroup("/loja/dominios").RequirePermission(Permission.StoreManage);

        domains.MapGet("/", async (ICustomDomains service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(ct)));

        domains.MapPost("/", async Task<Results<Ok<CustomDomainsView>, ProblemHttpResult>> (AddDomainRequest request, ICustomDomains service, CancellationToken ct) =>
        {
            try
            {
                return TypedResults.Ok(await service.AddAsync(request.Dominio, ct));
            }
            catch (ArgumentException e)
            {
                return AuthEndpoints.Problem(StatusCodes.Status400BadRequest, e.Message.Split(" (Parameter", 2)[0]);
            }
            catch (InvalidOperationException e)
            {
                return AuthEndpoints.Problem(StatusCodes.Status409Conflict, e.Message);
            }
        });

        domains.MapDelete("/{id:guid}", async Task<Results<Ok<CustomDomainsView>, ProblemHttpResult>> (Guid id, ICustomDomains service, CancellationToken ct) =>
            await service.RemoveAsync(id, ct) is { } view ? TypedResults.Ok(view) : AuthEndpoints.Problem(StatusCodes.Status404NotFound, "Domínio não encontrado."));

        domains.MapPost("/{id:guid}/verificar", async Task<Results<Ok<CustomDomainsView>, ProblemHttpResult>> (Guid id, ICustomDomains service, CancellationToken ct) =>
            await service.VerifyNowAsync(id, ct) is { } view ? TypedResults.Ok(view) : AuthEndpoints.Problem(StatusCodes.Status404NotFound, "Domínio não encontrado."));
    }

    /// <summary>Só com o DNS simulado (dev): define registros para testar a verificação sem DNS real.</summary>
    public static void MapFakeDns(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/dev/dns", Results<NoContent, BadRequest> (FakeDnsRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Host) || request.Tipo is not ("CNAME" or "A" or "TXT")) return TypedResults.BadRequest();
            FakeDnsResolver.Set(request.Host, request.Tipo, [.. request.Valores ?? []]);
            return TypedResults.NoContent();
        }).ExcludeFromDescription();
}
