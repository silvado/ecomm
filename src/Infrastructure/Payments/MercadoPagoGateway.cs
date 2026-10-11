using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecommerce.Application.Payments;
using Ecommerce.Domain.Payments;

namespace Ecommerce.Infrastructure.Payments;

public sealed class MercadoPagoOptions
{
    public string BaseUrl { get; set; } = "https://api.mercadopago.com";
}

/// <summary>
/// Mercado Pago (RF15) com as credenciais da conta da loja, guardadas no cofre: Access Token e chave secreta do webhook.
/// <list type="bullet">
/// <item>Checkout Pro: <c>POST /checkout/preferences</c> com itens em BRL, <c>back_urls</c> + <c>auto_return</c>,
/// <c>notification_url</c>, <c>external_reference</c> (id do pedido) e validade até o prazo da reserva; o comprador vai
/// ao <c>init_point</c>. Fonte: https://www.mercadopago.com.br/developers/en/reference/online-payments/checkout-pro-preferences/create-preference/post</item>
/// <item>Webhook: <c>x-signature</c> = <c>ts=...,v1=...</c>; <c>v1</c> é o HMAC-SHA256 (hex) do manifesto
/// <c>id:{data.id};request-id:{x-request-id};ts:{ts};</c>, com <c>data.id</c> da query em minúsculas e as partes
/// ausentes removidas. Fonte: https://www.mercadopago.com.br/developers/pt/docs/your-integrations/notifications/webhooks</item>
/// <item>Consulta: <c>GET /v1/payments/{id}</c> (status, <c>transaction_amount</c>, <c>external_reference</c>).
/// Fonte: https://www.mercadopago.com.br/developers/en/reference/online-payments/checkout-pro-preferences/get-payment/get</item>
/// </list>
/// O frete vai como mais um item (HIPÓTESE, Q29): o total cobrado é a soma dos itens, igual ao do pedido.
/// </summary>
public sealed class MercadoPagoGateway(HttpClient http, MercadoPagoOptions options, string accessToken, string webhookSecret, TimeProvider clock) : IPaymentGateway
{
    public const string GatewayName = "mercado-pago";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record Item(string Id, string Title, int Quantity, decimal UnitPrice, string CurrencyId);
    private sealed record Payer(string Name, string Email);
    private sealed record BackUrls(string Success, string Pending, string Failure);
    private sealed record PreferenceRequest(
        IReadOnlyList<Item> Items, Payer Payer, BackUrls BackUrls, string AutoReturn, string NotificationUrl, string ExternalReference,
        bool Expires, string ExpirationDateFrom, string ExpirationDateTo);
    private sealed record PreferenceResponse(string? Id, string? InitPoint);
    private sealed record PaymentResponse(long Id, string? Status, decimal? TransactionAmount, string? ExternalReference, string? PaymentTypeId);
    private sealed record NotificationBody(string? Type);

    public string Name => GatewayName;

    public async Task<PaymentCheckout> CreateCheckoutAsync(PaymentCheckoutRequest request, CancellationToken ct = default)
    {
        var items = request.Items.Select((i, n) => new Item($"item-{n + 1}", Title(i.Title), i.Quantity, i.UnitPrice, "BRL")).ToList();
        if (request.ShippingTotal > 0) items.Add(new Item("frete", "Frete", 1, request.ShippingTotal, "BRL"));

        var body = new PreferenceRequest(items, new Payer(request.BuyerName, request.BuyerEmail),
            new BackUrls(request.ReturnUrl, request.ReturnUrl, request.ReturnUrl), "approved", request.NotificationUrl,
            request.OrderId.ToString(), true,
            Iso(clock.GetUtcNow().AddMinutes(-1)), Iso(request.ExpiresAt));

        using var message = Request(HttpMethod.Post, "/checkout/preferences");
        message.Content = JsonContent.Create(body, options: Json);

        using var response = await SendAsync(message, ct);
        if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode);
        var preference = await response.Content.ReadFromJsonAsync<PreferenceResponse>(Json, ct);
        return preference?.InitPoint is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? new PaymentCheckout(url)
            : throw new PaymentGatewayException("Mercado Pago não devolveu o link de pagamento.");
    }

    public Task<PaymentNotification> ReadNotificationAsync(WebhookRequest request, CancellationToken ct = default)
    {
        if (!request.Headers.TryGetValue("x-signature", out var header) || Signature(header) is not ({ } ts, { } v1))
            return Task.FromResult(PaymentNotification.Invalid);

        var dataId = request.Query.GetValueOrDefault("data.id") is { Length: > 0 } id ? id.ToLowerInvariant() : null;
        var requestId = request.Headers.GetValueOrDefault("x-request-id") is { Length: > 0 } rid ? rid : null;
        var manifest = new StringBuilder();
        if (dataId is not null) manifest.Append("id:").Append(dataId).Append(';');
        if (requestId is not null) manifest.Append("request-id:").Append(requestId).Append(';');
        manifest.Append("ts:").Append(ts).Append(';');

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(webhookSecret), Encoding.UTF8.GetBytes(manifest.ToString()));
        if (!TryHex(v1, out var received) || !CryptographicOperations.FixedTimeEquals(expected, received))
            return Task.FromResult(PaymentNotification.Invalid);

        if (Type(request) != "payment" || dataId is null) return Task.FromResult(PaymentNotification.Other);
        // A mesma entrega reenviada traz o mesmo x-request-id; sem ele, a assinatura identifica a notificação.
        return Task.FromResult(new PaymentNotification(NotificationValidity.Payment, requestId ?? $"{ts}:{v1}", dataId));
    }

    public async Task<GatewayPayment?> GetPaymentAsync(string paymentId, CancellationToken ct = default)
    {
        // O id do Mercado Pago é numérico; qualquer outra coisa não vira caminho de URL.
        if (paymentId.Length is 0 or > 20 || !paymentId.All(char.IsAsciiDigit)) return null;

        using var message = Request(HttpMethod.Get, $"/v1/payments/{paymentId}");
        using var response = await SendAsync(message, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode);

        var payment = await response.Content.ReadFromJsonAsync<PaymentResponse>(Json, ct)
            ?? throw new PaymentGatewayException("Mercado Pago devolveu um pagamento vazio.");
        return new GatewayPayment(payment.Id.ToString(CultureInfo.InvariantCulture), Status(payment.Status), payment.TransactionAmount ?? 0m,
            payment.ExternalReference, payment.PaymentTypeId);
    }

    /// <summary>
    /// Status do Mercado Pago no nosso. Fonte: https://www.mercadopago.com.br/developers/en/docs/checkout-api-payments/response-handling/query-results
    /// <c>charged_back</c> (contestação no cartão) conta como estorno: pedido pago vai para análise.
    /// </summary>
    internal static PaymentStatus Status(string? status) => status switch
    {
        "approved" => PaymentStatus.Approved,
        "rejected" => PaymentStatus.Rejected,
        "cancelled" => PaymentStatus.Canceled,
        "refunded" or "charged_back" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending, // pending, authorized, in_process, in_mediation
    };

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var message = new HttpRequestMessage(method, new Uri(new Uri(options.BaseUrl), path));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return message;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct)
    {
        try { return await http.SendAsync(message, ct); }
        catch (HttpRequestException e) { throw new PaymentGatewayException("Mercado Pago indisponível.", e); }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested) { throw new PaymentGatewayException("Mercado Pago não respondeu a tempo.", e); }
    }

    private static PaymentGatewayException Failure(HttpStatusCode status) => status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
        ? new PaymentGatewayException("O Mercado Pago recusou as credenciais da loja.")
        : new PaymentGatewayException($"Mercado Pago respondeu {(int)status}.");

    private static (string? Ts, string? V1) Signature(string header)
    {
        string? ts = null, v1 = null;
        foreach (var part in header.Split(','))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair is not [var key, var value]) continue;
            if (key == "ts") ts = value;
            else if (key == "v1") v1 = value;
        }
        return (ts is { Length: > 0 } ? ts : null, v1 is { Length: > 0 } ? v1 : null);
    }

    private static bool TryHex(string text, out byte[] bytes)
    {
        try { bytes = Convert.FromHexString(text); return true; }
        catch (FormatException) { bytes = []; return false; }
    }

    /// <summary>O tópico vem na query (<c>type</c>) ou no corpo JSON (<c>type</c>).</summary>
    private static string? Type(WebhookRequest request)
    {
        if (request.Query.GetValueOrDefault("type") is { Length: > 0 } type) return type;
        try { return JsonSerializer.Deserialize<NotificationBody>(request.Body, Json)?.Type; }
        catch (JsonException) { return null; }
    }

    private static string Title(string title) => title.Length <= 250 ? title : title[..250];

    private static string Iso(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
}
