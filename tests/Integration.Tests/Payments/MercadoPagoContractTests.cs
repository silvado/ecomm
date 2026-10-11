using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecommerce.Application.Payments;
using Ecommerce.Domain.Payments;
using Ecommerce.Infrastructure.Payments;

namespace Ecommerce.Integration.Tests.Payments;

/// <summary>
/// Contrato do adaptador do Mercado Pago contra um servidor HTTP simulado, no formato da documentação oficial
/// (preferências do Checkout Pro, consulta de pagamento e assinatura do webhook). Nada sai para a internet.
/// </summary>
public sealed class MercadoPagoContractTests
{
    private const string Token = "APP_USR-0000000000000000-000000-token-da-loja";
    private const string Secret = "segredo-do-webhook-da-loja";
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("conexão recusada");
    }

    private static (MercadoPagoGateway Gateway, StubHandler Handler) Gateway(HttpStatusCode status = HttpStatusCode.OK, string body = "{}")
    {
        var handler = new StubHandler(status, body);
        return (new MercadoPagoGateway(new HttpClient(handler), new MercadoPagoOptions(), Token, Secret, new FixedClock(Now)), handler);
    }

    private static readonly PaymentCheckoutRequest Checkout = new(
        Guid.Parse("01a12944-e5ec-7501-af3a-6040e349f58b"), 42,
        [new PaymentCheckoutItem("Farol dianteiro esquerdo Gol G5", 1, 320m), new PaymentCheckoutItem("Lanterna traseira", 2, 45.25m)],
        60.5m, 471m, "Maria Silva", "maria@exemplo.com.br", Now.AddMinutes(30),
        "https://demo.localhost/pedido", "https://demo.localhost/api/webhooks/pagamentos/mercado-pago/01a11dca-18bf-7c71-ad1d-e06cf272724c");

    [Fact]
    public async Task Cria_preferencia_do_Checkout_Pro_e_devolve_o_init_point()
    {
        var (gateway, handler) = Gateway(body: """
            { "id": "202809963-920c288b", "init_point": "https://www.mercadopago.com.br/checkout/v1/redirect?pref_id=202809963-920c288b",
              "sandbox_init_point": "https://sandbox.mercadopago.com.br/checkout/v1/redirect?pref_id=202809963-920c288b" }
            """);

        var checkout = await gateway.CreateCheckoutAsync(Checkout);

        Assert.Equal("https://www.mercadopago.com.br/checkout/v1/redirect?pref_id=202809963-920c288b", checkout.RedirectUrl);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://api.mercadopago.com/checkout/preferences", handler.Request.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal(Token, handler.Request.Headers.Authorization.Parameter);

        var json = JsonDocument.Parse(handler.RequestBody!).RootElement;
        var items = json.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal("Farol dianteiro esquerdo Gol G5", items[0].GetProperty("title").GetString());
        Assert.Equal(2, items[1].GetProperty("quantity").GetInt32());
        Assert.Equal(45.25m, items[1].GetProperty("unit_price").GetDecimal());
        Assert.All(items, i => Assert.Equal("BRL", i.GetProperty("currency_id").GetString()));
        Assert.Equal("Frete", items[2].GetProperty("title").GetString());
        Assert.Equal(60.5m, items[2].GetProperty("unit_price").GetDecimal());
        // O total cobrado pelo Mercado Pago é a soma dos itens: igual ao do pedido.
        Assert.Equal(Checkout.Total, items.Sum(i => i.GetProperty("unit_price").GetDecimal() * i.GetProperty("quantity").GetInt32()));

        Assert.Equal(Checkout.OrderId.ToString(), json.GetProperty("external_reference").GetString());
        Assert.Equal(Checkout.NotificationUrl, json.GetProperty("notification_url").GetString());
        Assert.Equal("approved", json.GetProperty("auto_return").GetString());
        foreach (var kind in new[] { "success", "pending", "failure" })
            Assert.Equal(Checkout.ReturnUrl, json.GetProperty("back_urls").GetProperty(kind).GetString());
        Assert.Equal("maria@exemplo.com.br", json.GetProperty("payer").GetProperty("email").GetString());
        Assert.True(json.GetProperty("expires").GetBoolean());
        Assert.Equal(Checkout.ExpiresAt, DateTimeOffset.Parse(json.GetProperty("expiration_date_to").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(DateTimeOffset.Parse(json.GetProperty("expiration_date_from").GetString()!, System.Globalization.CultureInfo.InvariantCulture) <= Now);
    }

    [Fact]
    public async Task Pedido_sem_frete_nao_manda_item_de_frete()
    {
        var (gateway, handler) = Gateway(body: """{ "init_point": "https://www.mercadopago.com.br/checkout/v1/redirect?pref_id=1" }""");

        await gateway.CreateCheckoutAsync(Checkout with { ShippingTotal = 0m, Total = 410.5m });

        var items = JsonDocument.Parse(handler.RequestBody!).RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.DoesNotContain(items, i => i.GetProperty("title").GetString() == "Frete");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{}")]
    [InlineData(HttpStatusCode.BadRequest, """{ "message": "invalid_items" }""")]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    [InlineData(HttpStatusCode.OK, """{ "id": "1" }""")]
    [InlineData(HttpStatusCode.OK, """{ "init_point": "http://inseguro.exemplo/checkout" }""")]
    public async Task Falha_ao_criar_preferencia_vira_erro_do_gateway_sem_credencial_na_mensagem(HttpStatusCode status, string body)
    {
        var (gateway, _) = Gateway(status, body);

        var error = await Assert.ThrowsAsync<PaymentGatewayException>(() => gateway.CreateCheckoutAsync(Checkout));

        Assert.DoesNotContain(Token, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rede_fora_do_ar_vira_erro_do_gateway()
    {
        var gateway = new MercadoPagoGateway(new HttpClient(new ThrowingHandler()), new MercadoPagoOptions(), Token, Secret, new FixedClock(Now));

        await Assert.ThrowsAsync<PaymentGatewayException>(() => gateway.CreateCheckoutAsync(Checkout));
        await Assert.ThrowsAsync<PaymentGatewayException>(() => gateway.GetPaymentAsync("123"));
    }

    private static string Sign(string manifest, string secret = Secret) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(manifest)));

    private static WebhookRequest Notification(string? dataId, string? requestId, string signature, string? queryType = "payment", string body = "")
    {
        var headers = new Dictionary<string, string> { ["x-signature"] = signature };
        if (requestId is not null) headers["x-request-id"] = requestId;
        var query = new Dictionary<string, string>();
        if (dataId is not null) query["data.id"] = dataId;
        if (queryType is not null) query["type"] = queryType;
        return new WebhookRequest(headers, query, body);
    }

    [Fact]
    public async Task Notificacao_com_assinatura_valida_e_aceita()
    {
        var (gateway, handler) = Gateway();
        var signature = $"ts=1704908010,v1={Sign("id:123456;request-id:bb56a2f1-6aae-46ac-982e-9dcd3581d08e;ts:1704908010;")}";

        var notification = await gateway.ReadNotificationAsync(Notification("123456", "bb56a2f1-6aae-46ac-982e-9dcd3581d08e", signature));

        Assert.Equal(NotificationValidity.Payment, notification.Validity);
        Assert.Equal("123456", notification.PaymentId);
        Assert.Equal("bb56a2f1-6aae-46ac-982e-9dcd3581d08e", notification.IdempotencyKey);
        Assert.Equal(0, handler.Calls); // a conferência é local; a consulta fica para a fila
    }

    [Theory]
    [InlineData("ts=1704908010,v1=00")]
    [InlineData("ts=1704908010")]
    [InlineData("v1=abc")]
    [InlineData("lixo")]
    [InlineData("ts=1704908010,v1=zz-nao-e-hex")]
    public async Task Notificacao_com_assinatura_errada_ou_incompleta_e_recusada(string signature)
    {
        var (gateway, _) = Gateway();

        var notification = await gateway.ReadNotificationAsync(Notification("123456", "req-1", signature));

        Assert.Equal(NotificationValidity.Invalid, notification.Validity);
    }

    [Fact]
    public async Task Assinatura_com_outra_chave_ou_outro_pagamento_e_recusada()
    {
        var (gateway, _) = Gateway();
        var otherKey = $"ts=1,v1={Sign("id:123456;request-id:req-1;ts:1;", "outra-chave-qualquer")}";
        var otherPayment = $"ts=1,v1={Sign("id:999999;request-id:req-1;ts:1;")}";

        Assert.Equal(NotificationValidity.Invalid, (await gateway.ReadNotificationAsync(Notification("123456", "req-1", otherKey))).Validity);
        Assert.Equal(NotificationValidity.Invalid, (await gateway.ReadNotificationAsync(Notification("123456", "req-1", otherPayment))).Validity);
    }

    [Fact]
    public async Task Id_alfanumerico_entra_no_manifesto_em_minusculas()
    {
        var (gateway, _) = Gateway();
        var signature = $"ts=1,v1={Sign("id:ord01jq4s4ky8hwq6na5pxb65b3d3;request-id:req-1;ts:1;")}";

        var notification = await gateway.ReadNotificationAsync(Notification("ORD01JQ4S4KY8HWQ6NA5PXB65B3D3", "req-1", signature));

        Assert.NotEqual(NotificationValidity.Invalid, notification.Validity);
    }

    [Fact]
    public async Task Sem_x_request_id_o_manifesto_omite_a_parte_e_a_chave_vem_da_assinatura()
    {
        var (gateway, _) = Gateway();
        var v1 = Sign("id:123456;ts:1704908010;");

        var notification = await gateway.ReadNotificationAsync(Notification("123456", null, $"ts=1704908010,v1={v1}"));

        Assert.Equal(NotificationValidity.Payment, notification.Validity);
        Assert.Equal($"1704908010:{v1}", notification.IdempotencyKey);
    }

    [Fact]
    public async Task Topico_vem_da_query_ou_do_corpo_e_outros_topicos_sao_ignorados()
    {
        var (gateway, _) = Gateway();
        var signature = $"ts=1,v1={Sign("id:123456;request-id:req-1;ts:1;")}";

        var fromBody = await gateway.ReadNotificationAsync(Notification("123456", "req-1", signature, queryType: null,
            body: """{ "action": "payment.updated", "type": "payment", "data": { "id": "123456" } }"""));
        var otherTopic = await gateway.ReadNotificationAsync(Notification("123456", "req-1", signature, queryType: "merchant_order"));
        var noTopic = await gateway.ReadNotificationAsync(Notification("123456", "req-1", signature, queryType: null, body: "não é json"));

        Assert.Equal(NotificationValidity.Payment, fromBody.Validity);
        Assert.Equal(NotificationValidity.Other, otherTopic.Validity);
        Assert.Equal(NotificationValidity.Other, noTopic.Validity);
    }

    [Fact]
    public async Task Consulta_o_pagamento_e_le_valor_em_texto_ou_numero()
    {
        var (gateway, handler) = Gateway(body: """
            { "id": 123456, "status": "approved", "status_detail": "accredited", "transaction_amount": "24.50",
              "transaction_amount_refunded": 0, "external_reference": "01a12944-e5ec-7501-af3a-6040e349f58b",
              "payment_method_id": "visa", "payment_type_id": "credit_card", "currency_id": "BRL" }
            """);

        var payment = await gateway.GetPaymentAsync("123456");

        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Equal("https://api.mercadopago.com/v1/payments/123456", handler.Request.RequestUri!.ToString());
        Assert.Equal(Token, handler.Request.Headers.Authorization!.Parameter);
        Assert.Equal(new GatewayPayment("123456", PaymentStatus.Approved, 24.50m, "01a12944-e5ec-7501-af3a-6040e349f58b", "credit_card"), payment);

        var (numeric, _) = Gateway(body: """{ "id": 7, "status": "pending", "transaction_amount": 471.0, "external_reference": "x", "currency_id": "BRL" }""");
        Assert.Equal(471m, (await numeric.GetPaymentAsync("7"))!.Amount);
    }

    [Theory]
    [InlineData("approved", PaymentStatus.Approved)]
    [InlineData("rejected", PaymentStatus.Rejected)]
    [InlineData("cancelled", PaymentStatus.Canceled)]
    [InlineData("refunded", PaymentStatus.Refunded)]
    [InlineData("charged_back", PaymentStatus.Refunded)]
    [InlineData("pending", PaymentStatus.Pending)]
    [InlineData("authorized", PaymentStatus.Pending)]
    [InlineData("in_process", PaymentStatus.Pending)]
    [InlineData("in_mediation", PaymentStatus.Pending)]
    public async Task Status_do_Mercado_Pago_vira_o_nosso(string status, PaymentStatus expected)
    {
        var (gateway, _) = Gateway(body: $$"""{ "id": 1, "status": "{{status}}", "transaction_amount": 10, "currency_id": "BRL" }""");

        Assert.Equal(expected, (await gateway.GetPaymentAsync("1"))!.Status);
    }

    [Fact]
    public async Task Pagamento_inexistente_e_nulo_e_id_nao_numerico_nem_vai_ao_Mercado_Pago()
    {
        var (notFound, _) = Gateway(HttpStatusCode.NotFound, """{ "message": "Payment not found", "status": 404 }""");
        Assert.Null(await notFound.GetPaymentAsync("123"));

        var (gateway, handler) = Gateway();
        Assert.Null(await gateway.GetPaymentAsync("../users/me"));
        Assert.Null(await gateway.GetPaymentAsync(""));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Erro_na_consulta_vira_erro_do_gateway_para_a_fila_tentar_de_novo(HttpStatusCode status)
    {
        var (gateway, _) = Gateway(status);

        var error = await Assert.ThrowsAsync<PaymentGatewayException>(() => gateway.GetPaymentAsync("123"));

        Assert.DoesNotContain(Token, error.Message, StringComparison.Ordinal);
    }
}
