using Ecommerce.Application.Tenancy;
using Ecommerce.Infrastructure.Tenancy;

namespace Ecommerce.Api.Tenancy;

/// <summary>
/// Resolve o tenant da loja pelo cabeçalho Host (RF04). Aplicado ao grupo de rotas públicas da loja.
/// O Host já vem corrigido pelo middleware de forwarded headers, que só confia em proxies conhecidos.
/// </summary>
public sealed class StoreTenantFilter(ITenantCatalog catalog, TenantScope scope) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var tenant = await catalog.FindByHostAsync(http.Request.Host.Host, http.RequestAborted);

        // Mesma resposta para qualquer host desconhecido: não revela a existência de outros tenants (RF04 CA2).
        if (tenant is null)
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Loja não encontrada.");

        if (!tenant.IsStorefrontAvailable)
            return TypedResults.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Loja indisponível no momento.",
                type: StoreProblemTypes.StoreUnavailable);

        scope.Set(tenant.Id);
        http.Items[typeof(TenantDescriptor)] = tenant;
        return await next(context);
    }
}

public static class StoreProblemTypes
{
    /// <summary>Usado pelo storefront para exibir a página "loja indisponível" (RF04 CA3).</summary>
    public const string StoreUnavailable = "https://plataforma/problemas/loja-indisponivel";
}

public static class StoreTenantHttpContextExtensions
{
    /// <summary>Tenant resolvido por <see cref="StoreTenantFilter"/>.</summary>
    public static TenantDescriptor GetStoreTenant(this HttpContext http) =>
        http.Items[typeof(TenantDescriptor)] as TenantDescriptor
        ?? throw new InvalidOperationException("Rota sem StoreTenantFilter.");
}
