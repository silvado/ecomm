using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ecommerce.Domain.Platform;
using Ecommerce.Integration.Tests.Infrastructure;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>Montagem de loja para os testes da vitrine: peças pelo painel e configuração de entrega.</summary>
internal static class StoreHttp
{
    private sealed record IdDto(Guid Id);

    /// <summary>Cria a peça pelo painel e, se <paramref name="active"/>, envia uma foto e ativa (como o lojista faria).</summary>
    public static async Task<Guid> CreatePartAsync(this ApiFactory api, string token, decimal price = 100m, int quantity = 3,
        bool dimensions = true, bool active = true, string title = "Peça de teste")
    {
        var created = await SendAsync(api.PanelClient(), token, HttpMethod.Post, "/api/painel/pecas", new
        {
            internalCode = "K-" + Guid.NewGuid().ToString("N")[..8], title, condition = "used", price, quantity,
            lengthCm = dimensions ? 30 : (int?)null, widthCm = dimensions ? 20 : (int?)null, heightCm = dimensions ? 10 : (int?)null,
            weightG = dimensions ? 1500 : (int?)null, oemCodes = Array.Empty<string>(),
        });
        var id = (await created.Content.ReadFromJsonAsync<IdDto>())!.Id;
        if (active)
        {
            using var photo = new HttpRequestMessage(HttpMethod.Post, $"/api/painel/pecas/{id}/fotos") { Content = new ByteArrayContent(TestImages.Jpeg(120, 90)) };
            photo.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            await api.PanelClient().SendAsync(photo);
            await SendAsync(api.PanelClient(), token, HttpMethod.Put, $"/api/painel/pecas/{id}/situacao", new { status = "active" });
        }
        return id;
    }

    public static Task<HttpResponseMessage> ConfigureShippingAsync(this ApiFactory api, string token, Tenant tenant, string? origin, bool pickup, string? address) =>
        SendAsync(api.PanelClient(), token, HttpMethod.Put, "/api/painel/loja", new
        {
            tradeName = tenant.TradeName, primaryColor = "#1F5FBF", backgroundColor = "#FFFFFF", textColor = "#1D2330",
            shipping = new { originPostalCode = origin, pickupEnabled = pickup, pickupAddress = address },
        });

    /// <summary>Cliente da vitrine da loja; IP próprio para os limites por IP não se misturarem entre testes.</summary>
    public static HttpClient StoreClient(this ApiFactory api, Tenant tenant, string? remoteIp = null) =>
        api.ClientFor($"{tenant.Slug}.plataforma.test", remoteIp);
}
