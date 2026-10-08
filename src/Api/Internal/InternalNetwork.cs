using System.Net;

namespace Ecommerce.Api.Internal;

/// <summary>Redes de onde vêm o proxy (Caddy) e os serviços internos. Configuração: <c>Network:Internal</c> (CIDR).</summary>
public sealed class InternalNetworkOptions
{
    public const string Section = "Network";

    public string[] Internal { get; set; } = [];

    public bool Contains(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        return Internal.Any(cidr => IPNetwork.TryParse(cidr, out var network) && network.Contains(address));
    }
}

/// <summary>
/// Endpoints <c>/internal/*</c> só atendem chamadas diretas da rede interna (ex.: Caddy consultando o <c>ask</c>).
/// Requisições que passaram pelo proxy público trazem X-Forwarded-For e são recusadas antes de tudo.
/// </summary>
public static class InternalEndpointGuard
{
    public const string PathPrefix = "/internal";

    /// <summary>Registrar antes de UseForwardedHeaders (que consome os cabeçalhos X-Forwarded-*).</summary>
    public static IApplicationBuilder UseInternalEndpointGuard(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
        {
            if (http.Request.Path.StartsWithSegments(PathPrefix) && http.Request.Headers.ContainsKey("X-Forwarded-For"))
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next(http);
        });
}

public sealed class InternalNetworkFilter(Microsoft.Extensions.Options.IOptions<InternalNetworkOptions> options) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        options.Value.Contains(context.HttpContext.Connection.RemoteIpAddress)
            ? next(context)
            : ValueTask.FromResult<object?>(TypedResults.NotFound());
}
