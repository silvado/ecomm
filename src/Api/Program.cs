using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Ecommerce.Api.Auth;
using Ecommerce.Api.Internal;
using Ecommerce.Api.Panel;
using Ecommerce.Api.Tenancy;
using Ecommerce.Application.Catalog;
using Ecommerce.Application.Store;
using Ecommerce.Application.Tenancy;
using Ecommerce.Infrastructure;
using Ecommerce.Infrastructure.Messaging;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.HttpOverrides;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHealthChecks();
builder.UseWolverine(opts => opts.ConfigureMessaging(builder.Configuration.GetConnectionString("Tenants")!));

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

// Painel (RF07): JWT curto no corpo + renovação por cookie HttpOnly. Chave só por variável de ambiente.
var authOptions = builder.Configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.Section));
builder.Services.AddSingleton<AccessTokens>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = AccessTokens.ValidationParameters(authOptions);
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o =>
{
    // HIPÓTESE: 10 tentativas de login por minuto por IP, além do bloqueio por conta (RF07 CA4).
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(AuthEndpoints.LoginRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "desconhecido",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

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
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

// Rotas públicas da loja: tenant resolvido pelo Host (RF04).
var store = app.MapGroup("/api/loja").AddEndpointFilter<StoreTenantFilter>();
// Nome, tema e textos: o storefront SSR desenha a loja com isto a cada página (RF01 CA3, ADR-0006).
store.MapGet("/identidade", async (IStoreProfileService profiles, CancellationToken ct) => TypedResults.Ok(await profiles.GetPublicAsync(ct)));

// Busca por veículo (RF09 CA3): só peças ativas da loja do Host compatíveis com o modelo/versão/ano.
store.MapVehicleCatalog();
store.MapGet("/pecas", async Task<Results<Ok<StorePartsPage>, ProblemHttpResult>> (
    Guid? modelo, Guid? versao, int? ano, int? pagina, int? tamanho, IPartQueries queries, CancellationToken ct) =>
{
    if (VehicleEndpoints.VehicleFilterFrom(modelo, versao, ano) is not { } vehicle)
        return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Escolha o modelo do veículo.");
    var (items, total) = await queries.SearchStoreAsync(vehicle, pagina ?? 1, tamanho ?? 24, ct);
    return TypedResults.Ok(new StorePartsPage(items, total));
});

// Logo da loja (RF01 CA2): o id muda a cada troca, então a URL pode ficar em cache por um ano.
store.MapGet("/logo/{logoId:guid}", async Task<Results<FileStreamHttpResult, NotFound>> (
    Guid logoId, HttpResponse response, IStoreProfileService profiles, CancellationToken ct) =>
{
    if (await profiles.GetLogoAsync(logoId, ct) is not { } logo) return TypedResults.NotFound();
    response.Headers.CacheControl = "public, max-age=31536000, immutable";
    return PanelEndpoints.UploadedImage(response, logo);
});

app.MapAuthEndpoints();
app.MapPanelEndpoints();

// Consultado pelo Caddy antes de emitir certificado on-demand (ADR-0004): só domínios verificados de lojas disponíveis.
var internalApi = app.MapGroup(InternalEndpointGuard.PathPrefix).AddEndpointFilter<InternalNetworkFilter>().ExcludeFromDescription();
internalApi.MapGet("/tls/ask", async (string domain, ITenantCatalog catalog, CancellationToken ct) =>
{
    var tenant = await catalog.FindByHostAsync(domain, ct);
    return tenant is { IsStorefrontAvailable: true } ? Results.Ok() : Results.NotFound();
});

app.Run();

internal sealed record StorePartsPage(IReadOnlyList<StorePartSummary> Items, int Total);

/// <summary>Exposto para os testes de integração (WebApplicationFactory).</summary>
public partial class Program;
