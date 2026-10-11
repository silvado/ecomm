using System.Net;
using System.Text;
using System.Text.Json;
using Ecommerce.Application.Payments;
using Ecommerce.Domain.Payments;
using Ecommerce.Infrastructure.Payments;

namespace Ecommerce.Integration.Tests.Payments;

/// <summary>
/// Contrato do adaptador da Cielo (Checkout Cielo / Link de Pagamento) contra um servidor HTTP simulado, no formato da
/// documentação oficial (criação do checkout, token OAuth, consultas e notificações). Nada sai para a internet.
/// </summary>
public sealed class CieloContractTests
{
    private const string MerchantId = "4c1c5b5a-1d5f-4a3e-9a0b-6f1d2e3c4b5a";
    private const string ClientId = "b521b6b2-0000-4000-8000-client-id-loja";
    private const string ClientSecret = "08Qkje79-segredo-do-cliente-da-loja";
    private const string Base = "https://cieloecommerce.cielo.com.br/api/public";

    /// <summary>Responde por rota; guarda cada requisição com o corpo.</summary>
    private sealed class Router : HttpMessageHandler
    {
        public Dictionary<string, (HttpStatusCode Status, string Body)> Routes { get; } = new();
        public List<(HttpRequestMessage Request, string? Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            var key = $"{request.Method} {request.RequestUri!.AbsolutePath}";
            var (status, body) = Routes.TryGetValue(key, out var route) ? route : (HttpStatusCode.NotFound, "{}");
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("conexão recusada");
    }

    private static (CieloGateway Gateway, Router Router) Gateway()
    {
        var router = new Router();
        router.Routes["POST /api/public/v2/token"] = (HttpStatusCode.Created, """{ "access_token": "token-de-acesso", "token_type": "bearer", "expires_in": 1199 }""");
        return (new CieloGateway(new HttpClient(router), new CieloOptions(), MerchantId, ClientId, ClientSecret), router);
    }

    private static readonly PaymentCheckoutRequest Checkout = new(
        Guid.Parse("01a12944-e5ec-7501-af3a-6040e349f58b"), 42,
        [new PaymentCheckoutItem("Farol dianteiro esquerdo Gol G5", 1, 320m), new PaymentCheckoutItem("Lanterna traseira", 2, 45.25m)],
        60.5m, 471m, "Maria Silva", "maria@exemplo.com.br", DateTimeOffset.UtcNow.AddMinutes(30),
        "https://demo.localhost/pedido", "https://demo.localhost/api/webhooks/pagamentos/cielo/01a11dca-18bf-7c71-ad1d-e06cf272724c");

    private static string Order(string status, string orderNumber = "PED42", string type = "Pix") => $$"""
        { "merchantId": "{{MerchantId}}", "orderNumber": "{{orderNumber}}", "softDescriptor": "LOJA",
          "cart": { "items": [ { "name": "Farol", "description": "", "unitPrice": 32000, "quantity": 1, "type": "Asset" },
                               { "name": "Lanterna", "unitPrice": 4525, "quantity": 2, "type": "Asset" },
                               { "name": "Frete", "unitPrice": 6050, "quantity": 1, "type": "Service" } ] },
          "shipping": { "type": "WithoutShipping", "services": [] },
          "payment": { "status": "{{status}}", "tid": "1006993069", "nsu": "123456", "numberOfPayments": 1,
                       "createdDate": "2026-10-11T14:29:43.767", "type": "{{type}}", "errorcode": "00" },
          "customer": { "fullName": "Maria Silva", "email": "maria@exemplo.com.br" },
          "links": [ { "method": "GET", "rel": "self", "href": "{{Base}}/v2/orders/abc" } ] }
        """;

    [Fact]
    public async Task Cria_o_checkout_com_carrinho_em_centavos_e_devolve_o_link()
    {
        var (gateway, router) = Gateway();
        router.Routes["POST /api/public/v1/orders/"] = (HttpStatusCode.Created,
            """{ "settings": { "checkoutUrl": "https://cieloecommerce.cielo.com.br/transactional/v2/checkout?id=abc", "profile": "CheckoutCielo", "version": 1 } }""");

        var checkout = await gateway.CreateCheckoutAsync(Checkout);

        Assert.Equal("https://cieloecommerce.cielo.com.br/transactional/v2/checkout?id=abc", checkout.RedirectUrl);
        var (request, body) = Assert.Single(router.Calls);
        Assert.Equal($"{Base}/v1/orders/", request.RequestUri!.ToString());
        Assert.Equal(MerchantId, request.Headers.GetValues("MerchantId").Single());
        Assert.Null(request.Headers.Authorization);

        var json = JsonDocument.Parse(body!).RootElement;
        Assert.Equal("PED42", json.GetProperty("OrderNumber").GetString());
        var items = json.GetProperty("Cart").GetProperty("Items").EnumerateArray().ToList();
        Assert.Equal([32000L, 4525L, 6050L], items.Select(i => i.GetProperty("UnitPrice").GetInt64()));
        Assert.Equal([1, 2, 1], items.Select(i => i.GetProperty("Quantity").GetInt32()));
        Assert.Equal("Frete", items[2].GetProperty("Name").GetString());
        Assert.Equal(47100L, items.Sum(i => i.GetProperty("UnitPrice").GetInt64() * i.GetProperty("Quantity").GetInt32()));
        Assert.Equal("WithoutShipping", json.GetProperty("Shipping").GetProperty("Type").GetString());
        Assert.Equal("https://demo.localhost/pedido", json.GetProperty("Options").GetProperty("ReturnUrl").GetString());
        Assert.Equal("maria@exemplo.com.br", json.GetProperty("Customer").GetProperty("Email").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "{}")]
    [InlineData(HttpStatusCode.Unauthorized, "{}")]
    [InlineData(HttpStatusCode.Created, """{ "settings": {} }""")]
    [InlineData(HttpStatusCode.Created, """{ "settings": { "checkoutUrl": "http://inseguro.exemplo/checkout" } }""")]
    public async Task Falha_ao_criar_o_checkout_vira_erro_do_gateway(HttpStatusCode status, string body)
    {
        var (gateway, router) = Gateway();
        router.Routes["POST /api/public/v1/orders/"] = (status, body);

        var error = await Assert.ThrowsAsync<PaymentGatewayException>(() => gateway.CreateCheckoutAsync(Checkout));

        Assert.DoesNotContain(ClientSecret, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Consulta_pela_transacao_com_token_OAuth_e_soma_o_carrinho()
    {
        var (gateway, router) = Gateway();
        router.Routes["GET /api/public/v2/orders/e679db68148047c5a88e"] = (HttpStatusCode.OK, Order("Paid"));

        var payment = await gateway.GetPaymentAsync("e679db68148047c5a88e");

        Assert.Equal(new GatewayPayment("e679db68148047c5a88e", PaymentStatus.Approved, 471m, "PED42", "Pix"), payment);
        var (token, _) = router.Calls[0];
        Assert.Equal($"{Base}/v2/token", token.RequestUri!.ToString());
        Assert.Equal("Basic", token.Headers.Authorization!.Scheme);
        Assert.Equal($"{ClientId}:{ClientSecret}", Encoding.UTF8.GetString(Convert.FromBase64String(token.Headers.Authorization.Parameter!)));
        var (query, _) = router.Calls[1];
        Assert.Equal(("Bearer", "token-de-acesso"), (query.Headers.Authorization!.Scheme, query.Headers.Authorization.Parameter));
    }

    [Fact]
    public async Task Frete_cobrado_como_servico_entra_no_valor_pago()
    {
        var (gateway, router) = Gateway();
        router.Routes["GET /api/public/v2/orders/comfrete"] = (HttpStatusCode.OK, """
            { "orderNumber": "PED7", "cart": { "items": [ { "name": "Farol", "unitPrice": 32000, "quantity": 1 } ] },
              "shipping": { "type": "FixedAmount", "services": [ { "name": "Sedex", "price": 6050 } ] }, "payment": { "status": "Paid" } }
            """);

        Assert.Equal(380.5m, (await gateway.GetPaymentAsync("comfrete"))!.Amount);
    }

    [Theory]
    [InlineData("Paid", PaymentStatus.Approved)]
    [InlineData("Denied", PaymentStatus.Rejected)]
    [InlineData("Expired", PaymentStatus.Canceled)]
    [InlineData("Voided", PaymentStatus.Refunded)]
    [InlineData("Pending", PaymentStatus.Pending)]
    [InlineData("Authorized", PaymentStatus.Pending)]
    [InlineData("NotFinalized", PaymentStatus.Pending)]
    [InlineData("AuthorizedIdPayPending", PaymentStatus.Pending)]
    public async Task Status_da_Cielo_vira_o_nosso(string status, PaymentStatus expected)
    {
        var (gateway, router) = Gateway();
        router.Routes["GET /api/public/v2/orders/abc123"] = (HttpStatusCode.OK, Order(status));

        Assert.Equal(expected, (await gateway.GetPaymentAsync("abc123"))!.Status);
    }

    [Fact]
    public async Task Pelo_numero_do_pedido_vale_a_transacao_paga_mesmo_que_nao_seja_a_mais_recente()
    {
        var (gateway, router) = Gateway();
        router.Routes["GET /api/public/v2/merchantOrderNumber/PED42"] = (HttpStatusCode.OK, """
            [ { "$id": "1", "checkoutOrderNumber": "recente", "createdDate": "2026-10-11T14:40:00.000", "links": [] },
              { "$id": "2", "checkoutOrderNumber": "antiga", "createdDate": "2026-10-11T14:30:00.000", "links": [] } ]
            """);
        router.Routes["GET /api/public/v2/orders/recente"] = (HttpStatusCode.OK, Order("Denied", type: "CreditCard"));
        router.Routes["GET /api/public/v2/orders/antiga"] = (HttpStatusCode.OK, Order("Paid"));

        var payment = await gateway.GetPaymentAsync("pedido:PED42");

        Assert.Equal(("antiga", PaymentStatus.Approved, "PED42"), (payment!.ExternalId, payment.Status, payment.OrderReference));
    }

    [Fact]
    public async Task Transacao_inexistente_e_nula_e_id_fora_do_formato_nem_vai_a_Cielo()
    {
        var (gateway, router) = Gateway();
        Assert.Null(await gateway.GetPaymentAsync("naoexiste"));
        Assert.Null(await gateway.GetPaymentAsync("pedido:PED42"));

        router.Calls.Clear();
        Assert.Null(await gateway.GetPaymentAsync("../v2/token"));
        Assert.Null(await gateway.GetPaymentAsync("pedido:PED 42"));
        Assert.Null(await gateway.GetPaymentAsync(""));
        Assert.Empty(router.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Erro_no_token_ou_na_consulta_vira_erro_do_gateway_para_a_fila_tentar_de_novo(HttpStatusCode status)
    {
        var (badToken, tokenRouter) = Gateway();
        tokenRouter.Routes["POST /api/public/v2/token"] = (status, "{}");
        await Assert.ThrowsAsync<PaymentGatewayException>(() => badToken.GetPaymentAsync("abc123"));

        var (badQuery, queryRouter) = Gateway();
        queryRouter.Routes["GET /api/public/v2/orders/abc123"] = (status, "{}");
        var error = await Assert.ThrowsAsync<PaymentGatewayException>(() => badQuery.GetPaymentAsync("abc123"));
        Assert.DoesNotContain(ClientSecret, error.Message, StringComparison.Ordinal);

        var offline = new CieloGateway(new HttpClient(new ThrowingHandler()), new CieloOptions(), MerchantId, ClientId, ClientSecret);
        await Assert.ThrowsAsync<PaymentGatewayException>(() => offline.GetPaymentAsync("abc123"));
    }

    private static WebhookRequest Notification(string body) =>
        new(new Dictionary<string, string>(), new Dictionary<string, string>(), body);

    [Fact]
    public async Task Notificacao_em_formulario_aponta_a_transacao_e_status_diferentes_sao_processados()
    {
        var (gateway, router) = Gateway();
        const string Form = "checkout_cielo_order_number=e679db68148047c5a88e&amount=47100&order_number=PED42&payment_status={0}&test_transaction=false&customer_name=Maria+Silva";

        var pending = await gateway.ReadNotificationAsync(Notification(string.Format(System.Globalization.CultureInfo.InvariantCulture, Form, 1)));
        var paid = await gateway.ReadNotificationAsync(Notification(string.Format(System.Globalization.CultureInfo.InvariantCulture, Form, 2)));
        var again = await gateway.ReadNotificationAsync(Notification(string.Format(System.Globalization.CultureInfo.InvariantCulture, Form, 2)));

        Assert.Equal((NotificationValidity.Payment, "e679db68148047c5a88e"), (paid.Validity, paid.PaymentId));
        Assert.NotEqual(pending.IdempotencyKey, paid.IdempotencyKey);
        Assert.Equal(paid.IdempotencyKey, again.IdempotencyKey);
        Assert.Empty(router.Calls); // a consulta fica para a fila
    }

    [Fact]
    public async Task Notificacao_JSON_so_vale_com_o_MerchantId_da_loja_e_nunca_segue_a_URL_recebida()
    {
        var (gateway, router) = Gateway();

        var mine = await gateway.ReadNotificationAsync(Notification($$"""
            { "URL": "https://atacante.exemplo/roubo", "MerchantId": "{{MerchantId.ToUpperInvariant()}}", "MerchantOrderNumber": "PED42" }
            """));
        var other = await gateway.ReadNotificationAsync(Notification("""
            { "URL": "https://x", "MerchantId": "00000000-0000-0000-0000-000000000001", "MerchantOrderNumber": "PED42" }
            """));

        Assert.Equal((NotificationValidity.Payment, "pedido:PED42"), (mine.Validity, mine.PaymentId));
        Assert.Equal(NotificationValidity.Invalid, other.Validity);

        router.Routes["GET /api/public/v2/merchantOrderNumber/PED42"] = (HttpStatusCode.OK, "[]");
        Assert.Null(await gateway.GetPaymentAsync(mine.PaymentId!));
        Assert.DoesNotContain(router.Calls, c => c.Request.RequestUri!.Host != "cieloecommerce.cielo.com.br");
    }

    [Theory]
    [InlineData("", NotificationValidity.Invalid)]
    [InlineData("{ quebrado", NotificationValidity.Invalid)]
    [InlineData("lixo", NotificationValidity.Invalid)]
    [InlineData("order_number=PED42&payment_status=2", NotificationValidity.Other)]
    [InlineData("checkout_cielo_order_number=../v2/token&payment_status=2", NotificationValidity.Other)]
    public async Task Notificacao_mal_formada_nao_vira_consulta(string body, NotificationValidity expected)
    {
        var (gateway, _) = Gateway();

        Assert.Equal(expected, (await gateway.ReadNotificationAsync(Notification(body))).Validity);
    }
}
