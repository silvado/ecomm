using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Ecommerce.Integration.Tests.Payments;

/// <summary>
/// Cielo simulada para a API inteira nos testes: cria checkouts (guardando o corpo e o MerchantId), emite token e
/// responde consultas cadastradas pelo teste. Cada teste usa ids próprios, então a instância é compartilhada.
/// </summary>
public sealed class CieloStub : HttpMessageHandler
{
    public const string Host = "cieloecommerce.cielo.com.br";
    public const string CheckoutUrl = "https://cieloecommerce.cielo.com.br/transactional/v2/checkout?id=";
    private const string Prefix = "/api/public";

    private readonly ConcurrentDictionary<string, string> _orders = new();
    private readonly ConcurrentDictionary<string, string> _byOrderNumber = new();

    public ConcurrentQueue<(string? MerchantId, string Body)> Checkouts { get; } = new();

    /// <summary>Tokens emitidos, por credencial Basic (ClientId:ClientSecret).</summary>
    public ConcurrentQueue<string> TokenRequests { get; } = new();

    public void SetOrder(string checkoutId, string json) => _orders[checkoutId] = json;

    public void SetOrderNumber(string orderNumber, string json) => _byOrderNumber[orderNumber] = json;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Post && path == $"{Prefix}/v1/orders/")
        {
            Checkouts.Enqueue((request.Headers.TryGetValues("MerchantId", out var ids) ? ids.Single() : null,
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            return Json(HttpStatusCode.Created, $$"""{ "settings": { "checkoutUrl": "{{CheckoutUrl}}{{Guid.NewGuid():N}}", "profile": "CheckoutCielo", "version": 1 } }""");
        }
        if (request.Method == HttpMethod.Post && path == $"{Prefix}/v2/token")
        {
            TokenRequests.Enqueue(Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization!.Parameter!)));
            return Json(HttpStatusCode.Created, """{ "access_token": "token-de-teste", "token_type": "bearer", "expires_in": 1199 }""");
        }
        if (request.Method == HttpMethod.Get && request.Headers.Authorization?.Parameter == "token-de-teste")
        {
            if (path.StartsWith($"{Prefix}/v2/orders/", StringComparison.Ordinal) && _orders.TryGetValue(path[$"{Prefix}/v2/orders/".Length..], out var order))
                return Json(HttpStatusCode.OK, order);
            if (path.StartsWith($"{Prefix}/v2/merchantOrderNumber/", StringComparison.Ordinal)
                && _byOrderNumber.TryGetValue(path[$"{Prefix}/v2/merchantOrderNumber/".Length..], out var list))
                return Json(HttpStatusCode.OK, list);
        }
        return Json(HttpStatusCode.BadRequest, "{}");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

/// <summary>Cliente HTTP dos gateways nos testes: cada host vai para o seu simulador.</summary>
public sealed class GatewayStubs(MercadoPagoStub mercadoPago, CieloStub cielo) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var invoker = new HttpMessageInvoker(request.RequestUri!.Host == CieloStub.Host ? cielo : mercadoPago, disposeHandler: false);
        return invoker.SendAsync(request, cancellationToken);
    }
}
