using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecommerce.Application.Payments;
using Ecommerce.Domain.Payments;

namespace Ecommerce.Infrastructure.Payments;

public sealed class CieloOptions
{
    public string BaseUrl { get; set; } = "https://cieloecommerce.cielo.com.br/api/public";
}

/// <summary>
/// Checkout Cielo / Link de Pagamento (RF15) com as credenciais da loja no cofre: MerchantId, ClientId e ClientSecret.
/// <list type="bullet">
/// <item>Checkout: <c>POST {base}/v1/orders/</c> com o cabeçalho <c>MerchantId</c>, carrinho em centavos, <c>OrderNumber</c>
/// (letras e dígitos, até 20) e <c>Options.ReturnUrl</c>; o comprador vai ao <c>Settings.CheckoutUrl</c>.
/// Fonte: https://docs.cielo.com.br/link/v1.0-en/reference/creating-checkout-cielo-payment-page.md</item>
/// <item>Token: <c>POST {base}/v2/token</c> com Basic <c>ClientId:ClientSecret</c>; vale 20 minutos.
/// Fonte: https://docs.cielo.com.br/link/v1.0-en/reference/create-access-token.md</item>
/// <item>Consulta: <c>GET {base}/v2/orders/{checkout_cielo_order_number}</c> e <c>GET {base}/v2/merchantOrderNumber/{order_number}</c>
/// (lista: o número pode se repetir). Fontes: .../get-transaction-by-checkout_cielo_order_number.md e .../get-transaction-order_number.md</item>
/// <item>Notificação: a URL é configurada no portal da Cielo (uma por loja) e chega em POST (formulário, com
/// <c>checkout_cielo_order_number</c> e <c>payment_status</c>) ou JSON (<c>MerchantId</c>, <c>MerchantOrderNumber</c>, <c>URL</c>).
/// Fontes: .../transaction-completion-notification.md e .../notification-content.md</item>
/// </list>
/// A Cielo não assina as notificações: elas só disparam a consulta autenticada, que é o que vale (RF15 CA3). A URL que
/// vem no JSON nunca é seguida (a consulta vai sempre ao endereço fixo). A consulta não traz um total: o valor pago é a
/// soma do carrinho que nós mesmos enviamos (o frete vai como item, HIPÓTESE Q30).
/// </summary>
public sealed class CieloGateway(HttpClient http, CieloOptions options, string merchantId, string clientId, string clientSecret) : IPaymentGateway
{
    public const string GatewayName = "cielo";
    private const int MaxIdLength = 62;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = null,
    };

    private sealed record Item(string Name, long UnitPrice, int Quantity, string Type);
    private sealed record Cart(IReadOnlyList<Item> Items);
    private sealed record Shipping(string Type);
    private sealed record Customer(string FullName, string Email);
    private sealed record CheckoutOptions(string ReturnUrl);
    private sealed record OrderRequest(string OrderNumber, Cart Cart, Shipping Shipping, Customer Customer, CheckoutOptions Options);
    private sealed record Settings(string? CheckoutUrl);
    private sealed record OrderResponse(Settings? Settings);
    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string? AccessToken);
    private sealed record QueryItem(long UnitPrice, int Quantity);
    private sealed record QueryCart(List<QueryItem>? Items);
    private sealed record QueryService(long Price);
    private sealed record QueryShipping(List<QueryService>? Services);
    private sealed record QueryPayment(string? Status, string? Type);
    private sealed record QueryOrder(string? OrderNumber, QueryCart? Cart, QueryShipping? Shipping, QueryPayment? Payment);
    private sealed record OrderNumberEntry(string? CheckoutOrderNumber, DateTime? CreatedDate);
    private sealed record JsonNotification(string? MerchantId, string? MerchantOrderNumber);

    public string Name => GatewayName;

    public async Task<PaymentCheckout> CreateCheckoutAsync(PaymentCheckoutRequest request, CancellationToken ct = default)
    {
        var items = request.Items.Select(i => new Item(Truncate(i.Title, 128), Cents(i.UnitPrice), i.Quantity, "Asset")).ToList();
        if (request.ShippingTotal > 0) items.Add(new Item("Frete", Cents(request.ShippingTotal), 1, "Service"));
        var body = new OrderRequest(OrderReference.FromNumber(request.OrderNumber), new Cart(items), new Shipping("WithoutShipping"),
            new Customer(request.BuyerName, request.BuyerEmail), new CheckoutOptions(request.ReturnUrl));

        using var message = new HttpRequestMessage(HttpMethod.Post, Url("/v1/orders/")) { Content = JsonContent.Create(body, options: Json) };
        message.Headers.TryAddWithoutValidation("MerchantId", merchantId);
        using var response = await SendAsync(message, ct);
        if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<OrderResponse>(Json, ct);
        return created?.Settings?.CheckoutUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? new PaymentCheckout(url)
            : throw new PaymentGatewayException("Cielo não devolveu o link de pagamento.");
    }

    /// <summary>
    /// Sem assinatura para conferir: só o formato. Formulário → o id da transação; JSON → o número do pedido, aceito só
    /// com o MerchantId desta loja. Cada notificação é processada (a consulta é idempotente), exceto reentregas do mesmo
    /// status da mesma transação.
    /// </summary>
    public Task<PaymentNotification> ReadNotificationAsync(WebhookRequest request, CancellationToken ct = default)
    {
        var body = request.Body.Trim();
        if (body.StartsWith('{'))
        {
            JsonNotification? json;
            try { json = JsonSerializer.Deserialize<JsonNotification>(body, Json); }
            catch (JsonException) { return Task.FromResult(PaymentNotification.Invalid); }
            if (json is null || !Guid.TryParse(json.MerchantId, out var notified) || !Guid.TryParse(merchantId, out var mine) || notified != mine)
                return Task.FromResult(PaymentNotification.Invalid);
            return Task.FromResult(IsId(json.MerchantOrderNumber)
                ? new PaymentNotification(NotificationValidity.Payment, $"json:{json.MerchantOrderNumber}:{Guid.CreateVersion7():N}", $"pedido:{json.MerchantOrderNumber}")
                : PaymentNotification.Other);
        }

        var form = ParseForm(body);
        if (form.Count == 0) return Task.FromResult(PaymentNotification.Invalid);
        if (!form.TryGetValue("checkout_cielo_order_number", out var checkoutId) || !IsId(checkoutId)) return Task.FromResult(PaymentNotification.Other);
        var status = form.GetValueOrDefault("payment_status") is { Length: > 0 and <= 3 } s && s.All(char.IsAsciiDigit) ? s : "?";
        return Task.FromResult(new PaymentNotification(NotificationValidity.Payment, $"{checkoutId}:{status}", checkoutId));
    }

    public async Task<GatewayPayment?> GetPaymentAsync(string paymentId, CancellationToken ct = default)
    {
        var byOrderNumber = paymentId.StartsWith("pedido:", StringComparison.Ordinal);
        var id = byOrderNumber ? paymentId["pedido:".Length..] : paymentId;
        if (!IsId(id)) return null;
        var token = await TokenAsync(ct);
        return byOrderNumber ? await ByOrderNumberAsync(id, token, ct) : await ByCheckoutIdAsync(id, token, ct);
    }

    /// <summary>Paid no Checkout é capturado; Voided = cancelado pela loja depois de autorizado (estorno, se já pago).</summary>
    internal static PaymentStatus Status(string? status) => status switch
    {
        "Paid" => PaymentStatus.Approved,
        "Denied" => PaymentStatus.Rejected,
        "Expired" => PaymentStatus.Canceled,
        "Voided" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending, // Pending, Authorized, NotFinalized, AuthorizedIdPayPending
    };

    private async Task<GatewayPayment?> ByCheckoutIdAsync(string checkoutId, string token, CancellationToken ct)
    {
        using var message = Authorized(HttpMethod.Get, $"/v2/orders/{checkoutId}", token);
        using var response = await SendAsync(message, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) return null;
        if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode);
        var order = await response.Content.ReadFromJsonAsync<QueryOrder>(Json, ct);
        if (order?.Payment is null) return null;
        var cents = (order.Cart?.Items ?? []).Sum(i => i.UnitPrice * i.Quantity) + (order.Shipping?.Services ?? []).Sum(s => s.Price);
        return new GatewayPayment(checkoutId, Status(order.Payment.Status), cents / 100m, order.OrderNumber, order.Payment.Type);
    }

    /// <summary>O mesmo número pode ter várias transações (ex.: cartão negado e depois Pix pago): vale a paga, senão a mais recente.</summary>
    private async Task<GatewayPayment?> ByOrderNumberAsync(string orderNumber, string token, CancellationToken ct)
    {
        using var message = Authorized(HttpMethod.Get, $"/v2/merchantOrderNumber/{orderNumber}", token);
        using var response = await SendAsync(message, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) return null;
        if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode);
        var entries = await response.Content.ReadFromJsonAsync<List<OrderNumberEntry>>(Json, ct) ?? [];

        var payments = new List<GatewayPayment>();
        foreach (var entry in entries.Where(e => IsId(e.CheckoutOrderNumber)).OrderByDescending(e => e.CreatedDate).Take(5))
            if (await ByCheckoutIdAsync(entry.CheckoutOrderNumber!, token, ct) is { } payment) payments.Add(payment);
        return payments.FirstOrDefault(p => p.Status == PaymentStatus.Approved) ?? payments.FirstOrDefault();
    }

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, Url("/v2/token")) { Content = new StringContent(string.Empty, Encoding.UTF8, "application/x-www-form-urlencoded") };
        message.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));
        using var response = await SendAsync(message, ct);
        if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(Json, ct))?.AccessToken is { Length: > 0 } token
            ? token
            : throw new PaymentGatewayException("Cielo não devolveu o token de acesso.");
    }

    private HttpRequestMessage Authorized(HttpMethod method, string path, string token)
    {
        var message = new HttpRequestMessage(method, Url(path));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return message;
    }

    private Uri Url(string path) => new(options.BaseUrl.TrimEnd('/') + path);

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct)
    {
        try { return await http.SendAsync(message, ct); }
        catch (HttpRequestException e) { throw new PaymentGatewayException("Cielo indisponível.", e); }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested) { throw new PaymentGatewayException("Cielo não respondeu a tempo.", e); }
    }

    private static PaymentGatewayException Failure(HttpStatusCode status) => status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
        ? new PaymentGatewayException("A Cielo recusou as credenciais da loja.")
        : new PaymentGatewayException($"Cielo respondeu {(int)status}.");

    /// <summary>Ids da Cielo e números de pedido: só letras e dígitos — nada vira outro caminho de URL.</summary>
    private static bool IsId(string? value) => value is { Length: > 0 and <= MaxIdLength } && value.All(char.IsAsciiLetterOrDigit);

    private static long Cents(decimal value) => (long)decimal.Round(value * 100m, 0, MidpointRounding.AwayFromZero);

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    private static Dictionary<string, string> ParseForm(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2) continue;
            try { result[Unescape(parts[0])] = Unescape(parts[1]); }
            catch (UriFormatException) { }
        }
        return result;

        static string Unescape(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
    }
}
