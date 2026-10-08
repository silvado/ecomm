using Ecommerce.Api.Internal;
using Ecommerce.Api.Tenancy;
using Ecommerce.Application.Tenancy;
using Ecommerce.Infrastructure;
using Ecommerce.Infrastructure.Messaging;
using Microsoft.AspNetCore.HttpOverrides;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHealthChecks();
builder.UseWolverine(opts => opts.ConfigureMessaging(builder.Configuration.GetConnectionString("Tenants")!));

builder.Services.Configure<InternalNetworkOptions>(builder.Configuration.GetSection(InternalNetworkOptions.Section));
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    // O Host da loja chega pelo proxy (Caddy → storefront SSR → API). Só confiamos em proxies da rede interna.
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    foreach (var cidr in builder.Configuration.GetSection($"{InternalNetworkOptions.Section}:Internal").Get<string[]>() ?? [])
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
});

var app = builder.Build();

app.UseInternalEndpointGuard();
app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

// Rotas públicas da loja: tenant resolvido pelo Host (RF04).
var store = app.MapGroup("/api/loja").AddEndpointFilter<StoreTenantFilter>();
store.MapGet("/identidade", (HttpContext http) =>
{
    var tenant = http.GetStoreTenant();
    return TypedResults.Ok(new StoreIdentity(tenant.Slug, tenant.TradeName));
});

// Consultado pelo Caddy antes de emitir certificado on-demand (ADR-0004): só domínios verificados de lojas disponíveis.
var internalApi = app.MapGroup(InternalEndpointGuard.PathPrefix).AddEndpointFilter<InternalNetworkFilter>().ExcludeFromDescription();
internalApi.MapGet("/tls/ask", async (string domain, ITenantCatalog catalog, CancellationToken ct) =>
{
    var tenant = await catalog.FindByHostAsync(domain, ct);
    return tenant is { IsStorefrontAvailable: true } ? Results.Ok() : Results.NotFound();
});

app.Run();

internal sealed record StoreIdentity(string Slug, string Name);

/// <summary>Exposto para os testes de integração (WebApplicationFactory).</summary>
public partial class Program;
