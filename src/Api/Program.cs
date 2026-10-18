using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Ecommerce.Api.Auth;
using Ecommerce.Api.Billing;
using Ecommerce.Api.Channels;
using Ecommerce.Api.Customers;
using Ecommerce.Api.Diagnostics;
using Ecommerce.Api.Internal;
using Ecommerce.Api.Panel;
using Ecommerce.Api.Payments;
using Ecommerce.Api.Platform;
using Ecommerce.Api.Tenancy;
using Ecommerce.Application.Catalog;
using Ecommerce.Application.Orders;
using Ecommerce.Application.Shipping;
using Ecommerce.Application.Store;
using Ecommerce.Application.Tenancy;
using Ecommerce.Infrastructure;
using Ecommerce.Infrastructure.Logging;
using Ecommerce.Infrastructure.Messaging;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.HttpOverrides;
using Serilog;
using Serilog.Events;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

// Logs JSON com tenant, correlação e redação de dados sensíveis (RNF06).
// preserveStaticLogger: cada host usa o próprio logger (sem o Log.Logger estático, que vários hosts no mesmo processo —
// testes em paralelo — compartilhariam, misturando os logs de um no outro).
builder.Services.AddSerilog((services, logger) => logger.ConfigureEcommerce(builder.Configuration), preserveStaticLogger: true, writeToProviders: true);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHealthChecks();
builder.UseWolverine(opts => opts.ConfigureMessaging(builder.Configuration.GetConnectionString("Tenants")!,
    retryDelays: MessagingSettings.RetryDelays(builder.Configuration)));

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
// Superadmin (ADR-0007): outra audiência, MFA obrigatório.
PlatformTokens.AddPlatformAuthentication(builder.Services, authOptions);
builder.Services.AddRateLimiter(o =>
{
    // HIPÓTESE: 10 tentativas de login por minuto por IP, além do bloqueio por conta (RF07 CA4).
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Cotação chama o Melhor Envio: limite por IP contra abuso (HIPÓTESE: 30 por minuto).
    o.AddPolicy("frete", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "desconhecido",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
    // Cada pedido reserva peças por 30 min: limite por IP contra quem tente esvaziar o estoque (HIPÓTESE: 10 por minuto).
    o.AddPolicy("pedido", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "desconhecido",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    // Webhooks de pagamento: a Cielo não assina os avisos (cada um vira uma consulta ao gateway). HIPÓTESE: 120 por minuto por IP.
    o.AddPolicy("webhook", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "desconhecido",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
    // Avisos de canais (RF21): o ML manda de dezenas de IPs e desativa os tópicos de quem não responde 200. HIPÓTESE: 600 por minuto por IP.
    o.AddPolicy(Ecommerce.Api.Channels.ChannelWebhookEndpoints.RateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "desconhecido",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1) }));
    // Área do cliente (RF16): pedir link (pode virar e-mail) e abrir sessão (adivinhar link). HIPÓTESE: 10 por minuto por IP,
    // além do limite de links por e-mail.
    o.AddPolicy(CustomerEndpoints.RateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "desconhecido",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    // Teste de e-mail no painel: envio imediato por usuário (HIPÓTESE: 5 por minuto).
    o.AddPolicy(EmailEndpoints.EmailTestRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "desconhecido",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1) }));
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

app.UseCorrelation();
app.UseSerilogRequestLogging(o => o.GetLevel = (http, _, error) =>
    error is not null || http.Response.StatusCode >= 500 ? LogEventLevel.Error
    : http.Request.Path.StartsWithSegments("/health") ? LogEventLevel.Verbose
    : LogEventLevel.Information);
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
// RNF04: política de privacidade e termos (os da loja ou o modelo padrão).
store.MapGet("/privacidade", async (IStoreProfileService profiles, HttpResponse response, CancellationToken ct) =>
{
    response.Headers.CacheControl = "public, max-age=60";
    return TypedResults.Ok(await profiles.GetPublicLegalTextsAsync(ct));
});
// RF40 CA1: páginas exclusivas da loja personalizada (HTML já limpo ao salvar).
store.MapGet("/paginas/{slug}", async Task<Results<Ok<PublicPage>, NotFound>> (string slug, IStoreProfileService profiles, HttpResponse response, CancellationToken ct) =>
{
    if (await profiles.GetPublicPageAsync(slug, ct) is not { } page) return TypedResults.NotFound();
    response.Headers.CacheControl = "public, max-age=30";
    return TypedResults.Ok(page);
});

// Vitrine (RF13): só peças ativas da loja do Host; busca por texto (sem acento), código/OEM e veículo (RF09 CA3).
store.MapVehicleCatalog();
store.MapGet("/pecas", async (string? busca, Guid? modelo, Guid? versao, int? ano, int? pagina, int? tamanho,
    IStoreCatalog catalog, HttpResponse response, CancellationToken ct) =>
{
    response.Headers.CacheControl = "public, max-age=30";
    return TypedResults.Ok(await catalog.SearchAsync(
        new StoreSearch(busca, VehicleEndpoints.VehicleFilterFrom(modelo, versao, ano), pagina ?? 1, tamanho ?? 24), ct));
});
store.MapGet("/pecas/{id:guid}", async Task<Results<Ok<StorePartView>, ProblemHttpResult>> (
    Guid id, IStoreCatalog catalog, HttpResponse response, CancellationToken ct) =>
{
    if (await catalog.GetAsync(id, ct) is not { } part)
        return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Peça não encontrada.");
    response.Headers.CacheControl = "public, max-age=30";
    return TypedResults.Ok(part);
});
// Foto de peça ativa: o id muda a cada foto, então pode ficar em cache por um ano.
store.MapGet("/fotos/{photoId:guid}/{size:int}", async Task<Results<FileStreamHttpResult, NotFound>> (
    Guid photoId, int size, IStoreCatalog catalog, HttpResponse response, CancellationToken ct) =>
{
    if (await catalog.GetPhotoAsync(photoId, size, ct) is not { } photo) return TypedResults.NotFound();
    response.Headers.CacheControl = "public, max-age=31536000, immutable";
    return PanelEndpoints.UploadedImage(response, photo);
});
// Foto em JPEG para o catálogo da Meta (RF22: não aceita WebP). Mesma foto, mesmo id: cache longo.
store.MapGet("/fotos/{photoId:guid}/catalogo.jpg", async Task<Results<FileContentHttpResult, NotFound>> (
    Guid photoId, IStoreCatalog catalog, HttpResponse response, CancellationToken ct) =>
{
    if (await catalog.GetChannelImageAsync(photoId, ct) is not { } jpeg) return TypedResults.NotFound();
    response.Headers.CacheControl = "public, max-age=31536000, immutable";
    return TypedResults.File(jpeg, "image/jpeg");
});
// Carrinho (RF14): o navegador manda só peça e quantidade; preço, estoque e frete vêm sempre daqui.
store.MapPost("/carrinho", async (CartRequest request, IStorefrontCart cart, CancellationToken ct) =>
    TypedResults.Ok(await cart.CheckAsync(request.Items ?? [], ct)));
store.MapPost("/frete", async Task<Results<Ok<ShippingQuote>, ProblemHttpResult>> (ShippingRequest request, IStorefrontCart cart, CancellationToken ct) =>
{
    var (quote, error) = await cart.QuoteAsync(request.Cep ?? string.Empty, request.Items ?? [], ct);
    return quote is not null ? TypedResults.Ok(quote) : TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: error);
}).RequireRateLimiting("frete");
// Checkout como convidado (RF14 CA3): cria o pedido aguardando pagamento com as peças reservadas (RF12).
// 409: algo mudou desde o carrinho (preço, estoque, frete, total) e o comprador precisa rever antes de confirmar.
store.MapPost("/pedidos", async Task<Results<Created<PlacedOrder>, ProblemHttpResult>> (CheckoutRequest request, IStorefrontCheckout checkout, CancellationToken ct) =>
{
    var result = await checkout.PlaceAsync(request, ct);
    return result.Outcome switch
    {
        CheckoutOutcome.Placed => TypedResults.Created((string?)null, result.Order!),
        CheckoutOutcome.Changed => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: result.Message),
        _ => TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: result.Message),
    };
}).RequireRateLimiting("pedido");
store.MapStorePayment();
store.MapCustomerArea();
// Acompanhamento pelo link do comprador. Token no corpo (não na URL) para não ir parar em logs de acesso.
store.MapPost("/pedidos/consulta", async Task<Results<Ok<StoreOrderView>, NotFound>> (OrderLookupRequest request, IStorefrontCheckout checkout, HttpResponse response, CancellationToken ct) =>
{
    response.Headers.CacheControl = "no-store";
    return await checkout.FindAsync(request.Token, ct) is { } order ? TypedResults.Ok(order) : TypedResults.NotFound();
}).RequireRateLimiting("frete");

// Logo da loja (RF01 CA2): o id muda a cada troca, então a URL pode ficar em cache por um ano.
store.MapGet("/logo/{logoId:guid}", async Task<Results<FileStreamHttpResult, NotFound>> (
    Guid logoId, HttpResponse response, IStoreProfileService profiles, CancellationToken ct) =>
{
    if (await profiles.GetLogoAsync(logoId, ct) is not { } logo) return TypedResults.NotFound();
    response.Headers.CacheControl = "public, max-age=31536000, immutable";
    return PanelEndpoints.UploadedImage(response, logo);
});

app.MapPaymentWebhooks();
if (app.Configuration.GetValue<bool>("Payments:UseFakeGateway"))
{
    // A simulação marca pedidos como pagos sem dinheiro algum: jamais em produção.
    if (app.Environment.IsProduction()) throw new InvalidOperationException("Payments:UseFakeGateway não pode ser usado em produção.");
    app.MapFakePaymentSimulation();
}

app.MapSubscriptionWebhook();
app.MapMercadoLivreWebhook();
// Feed do catálogo da Meta (RF22 CA1): público, num endereço com o segredo da loja (cadastrado no Commerce Manager).
app.MapGet("/api/catalogo/{tenantId:guid}/{token}.csv", async Task<Results<ContentHttpResult, NotFound>> (
    Guid tenantId, string token, Ecommerce.Infrastructure.Tenancy.TenantScope scope, Ecommerce.Application.Channels.IMetaCatalogFeed feed,
    HttpResponse response, CancellationToken ct) =>
{
    scope.Set(tenantId);
    if (await feed.CsvAsync(tenantId, token, ct) is not { } csv) return TypedResults.NotFound();
    response.Headers.CacheControl = "no-store";
    return TypedResults.Content(csv, "text/csv; charset=utf-8");
}).RequireRateLimiting("webhook").ExcludeFromDescription();
if (app.Configuration.GetValue<bool>("Billing:UseFakeGateway"))
{
    // Cobrança simulada da assinatura (RF38): quita faturas sem dinheiro algum. Jamais em produção.
    if (app.Environment.IsProduction()) throw new InvalidOperationException("Billing:UseFakeGateway não pode ser usado em produção.");
    app.MapFakeSubscriptionPayment();
}

if (app.Configuration.GetValue<bool>("Channels:UseFakeChannel"))
{
    // Canal simulado (RF26): aceita avisos de qualquer um, sem assinatura. Jamais em produção.
    if (app.Environment.IsProduction()) throw new InvalidOperationException("Channels:UseFakeChannel não pode ser usado em produção.");
    app.MapFakeChannelWebhook();
}

if (app.Configuration.GetValue<bool>("MercadoLivre:UseFake"))
{
    // Mercado Livre simulado (RF21): qualquer "vendedor" autoriza. Jamais em produção.
    if (app.Environment.IsProduction()) throw new InvalidOperationException("MercadoLivre:UseFake não pode ser usado em produção.");
    app.MapFakeMercadoLivreAuthorization();
}

if (app.Environment.IsProduction() && app.Configuration.GetValue<bool>("Meta:UseFake"))
    throw new InvalidOperationException("Meta:UseFake não pode ser usado em produção.");

if (app.Configuration.GetValue<bool>("Dns:UseFake"))
{
    // DNS simulado (RF03): qualquer um "prova" um domínio. Jamais em produção.
    if (app.Environment.IsProduction()) throw new InvalidOperationException("Dns:UseFake não pode ser usado em produção.");
    app.MapFakeDns();
}

// IA simulada devolve sugestões vazias: jamais em produção (lá, "anthropic" com a chave da plataforma, ou desligada).
if (app.Environment.IsProduction() && app.Configuration["Ai:Provider"] == Ecommerce.Infrastructure.Ai.AiOptions.FakeProvider)
    throw new InvalidOperationException("Ai:Provider=simulado não pode ser usado em produção.");

// A NF-e simulada não tem valor fiscal: jamais em produção.
if (app.Environment.IsProduction() && app.Configuration.GetValue<bool>("Fiscal:UseFakeProvider"))
    throw new InvalidOperationException("Fiscal:UseFakeProvider não pode ser usado em produção.");

app.MapAuthEndpoints();
app.MapPanelEndpoints();
app.MapPlatformAuthEndpoints();
app.MapPlatformEndpoints();

// Consultado pelo Caddy antes de emitir certificado on-demand (ADR-0004): só domínios verificados de lojas disponíveis
// ou em implantação (RF05: a página "em breve" também precisa de https).
var internalApi = app.MapGroup(InternalEndpointGuard.PathPrefix).AddEndpointFilter<InternalNetworkFilter>().ExcludeFromDescription();
internalApi.MapGet("/tls/ask", async (string domain, ITenantCatalog catalog, CancellationToken ct) =>
{
    var tenant = await catalog.FindByHostAsync(domain, ct);
    return tenant is { IsStorefrontAvailable: true } or { Status: Ecommerce.Domain.Platform.TenantStatus.Onboarding } ? Results.Ok() : Results.NotFound();
});

// RNF06: fila em números para o monitoramento da VPS (só rede interna).
internalApi.MapGet("/metricas", async (QueueMetrics metrics, CancellationToken ct) => TypedResults.Ok(await metrics.ReadAsync(ct)));
// RF24: latência da propagação de estoque medida neste processo (a API e o Worker processam a fila).
internalApi.MapGet("/metricas/canais", (Ecommerce.Infrastructure.Channels.ChannelSyncMetrics metrics) =>
{
    var (count, late, max) = metrics.Snapshot();
    return TypedResults.Ok(new { sincronizacoes = count, acimaDe60s = late, maiorLatenciaSegundos = Math.Round(max.TotalSeconds, 1) });
});

app.Run();

internal sealed record CartRequest(IReadOnlyList<CartItem>? Items);
internal sealed record ShippingRequest(string? Cep, IReadOnlyList<CartItem>? Items);
internal sealed record OrderLookupRequest(string? Token);

/// <summary>Exposto para os testes de integração (WebApplicationFactory).</summary>
public partial class Program;
