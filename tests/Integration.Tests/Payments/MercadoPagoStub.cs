using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Ecommerce.Integration.Tests.Payments;

/// <summary>
/// Mercado Pago simulado para a API inteira nos testes (cliente HTTP nomeado do adaptador): cria preferências e responde
/// consultas de pagamento cadastradas pelo teste. Cada teste usa ids próprios, então a instância é compartilhada.
/// </summary>
public sealed class MercadoPagoStub : HttpMessageHandler
{
    public const string CheckoutUrl = "https://www.mercadopago.com.br/checkout/v1/redirect?pref_id=";

    private readonly ConcurrentDictionary<string, string> _payments = new();

    /// <summary>Corpo de cada preferência criada, com o token que veio no Authorization.</summary>
    public ConcurrentQueue<(string? Token, string Body)> Preferences { get; } = new();

    public void SetPayment(long id, string json) => _payments[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = json;

    public static string Sign(string secret, string dataId, string requestId, string ts) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"id:{dataId};request-id:{requestId};ts:{ts};")));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Post && path == "/checkout/preferences")
        {
            Preferences.Enqueue((request.Headers.Authorization?.Parameter, await request.Content!.ReadAsStringAsync(cancellationToken)));
            var id = Guid.NewGuid().ToString("N");
            return Json(HttpStatusCode.Created, $$"""{ "id": "{{id}}", "init_point": "{{CheckoutUrl}}{{id}}" }""");
        }
        if (request.Method == HttpMethod.Get && path.StartsWith("/v1/payments/", StringComparison.Ordinal)
            && _payments.TryGetValue(path["/v1/payments/".Length..], out var payment))
            return Json(HttpStatusCode.OK, payment);
        return Json(HttpStatusCode.NotFound, """{ "message": "Payment not found", "status": 404 }""");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
