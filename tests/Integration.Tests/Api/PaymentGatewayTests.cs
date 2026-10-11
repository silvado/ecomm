using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Payments;
using Ecommerce.Integration.Tests.Infrastructure;
using Ecommerce.Integration.Tests.Payments;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF15 com gateways reais (Mercado Pago e Cielo, simulados por HTTP): o Dono cola as credenciais (cofre, nunca
/// devolvidas), a loja cobra pelo gateway salvo por último e o pedido só vira pago pela consulta ao gateway.
/// </summary>
public sealed class PaymentGatewayTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private const string Path = "/api/painel/pagamentos";

    private sealed record HintDto(string Field, string Hint);
    private sealed record SettingsDto(string Gateway, bool Configured, bool Active, List<HintDto> Credentials, DateTimeOffset? UpdatedAt, string NotificationUrl);
    private sealed record SecretDto(string Kind, string Name, string Hint);
    private sealed record PlacedDto(long Number, decimal Total);
    private sealed record RedirectDto(string RedirectUrl);
    private sealed record SummaryDto(Guid Id, long Number);
    private sealed record PageDto(List<SummaryDto> Items);
    private sealed record PaymentDto(string Gateway, string ExternalId, string Status, decimal Amount);
    private sealed record OrderDto(Guid Id, string Status, decimal Total, DateTimeOffset? PaidAt, List<PaymentDto> Payments);

    private sealed record Shop(Tenant Tenant, string Owner, HttpClient Store);
    private sealed record MercadoPago(string AccessToken, string WebhookSecret);
    private sealed record Cielo(string MerchantId, string ClientId, string ClientSecret);
    private sealed record Order(Guid Id, long Number, string Token, decimal Total);

    private static int _ip;
    private static long _paymentId = 9_000_000_000 + RandomNumberGenerator.GetInt32(1_000_000);

    private static string Random(string prefix) => prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));

    private static MercadoPago NewMercadoPago() => new(Random("APP_USR-"), Random(""));

    private static Cielo NewCielo() => new(Guid.NewGuid().ToString(), Random("cid-"), Random("csec-"));

    private async Task<Shop> ShopAsync()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        await api.ConfigureShippingAsync(owner, tenant, "01310-100", pickup: true, "Balcão");
        return new Shop(tenant, owner, api.StoreClient(tenant, $"198.51.102.{Interlocked.Increment(ref _ip) % 250 + 1}"));
    }

    private Task<HttpResponseMessage> SaveAsync(string session, string gateway, object credentials) =>
        SendAsync(api.PanelClient(), session, HttpMethod.Put, $"{Path}/{gateway}", credentials);

    private async Task<Shop> ConnectAsync(Shop shop, MercadoPago account)
    {
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(shop.Owner, "mercado-pago", new { account.AccessToken, account.WebhookSecret })).StatusCode);
        return shop;
    }

    private async Task<Shop> ConnectAsync(Shop shop, Cielo account)
    {
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(shop.Owner, "cielo", new { account.MerchantId, account.ClientId, account.ClientSecret })).StatusCode);
        return shop;
    }

    private async Task<Dictionary<string, SettingsDto>> SettingsAsync(string session) =>
        (await (await GetAsync(api.PanelClient(), session, Path)).Content.ReadFromJsonAsync<List<SettingsDto>>())!.ToDictionary(s => s.Gateway);

    private async Task<Order> OrderAsync(Shop shop, decimal price)
    {
        var part = await api.CreatePartAsync(shop.Owner, price: price, quantity: 2);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var response = await shop.Store.PostAsJsonAsync("/api/loja/pedidos", new
        {
            token, expectedTotal = price, buyer = TestBuyers.Buyer(),
            items = new[] { new { partId = part, quantity = 1 } }, delivery = new { method = "pickup" },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var number = (await response.Content.ReadFromJsonAsync<PlacedDto>())!.Number;
        var page = (await (await GetAsync(api.PanelClient(), shop.Owner, "/api/painel/pedidos")).Content.ReadFromJsonAsync<PageDto>())!;
        return new Order(page.Items.Single(o => o.Number == number).Id, number, token, price);
    }

    private static async Task<string> PayAsync(Shop shop, Order order)
    {
        var start = await shop.Store.PostAsJsonAsync("/api/loja/pedidos/pagamento", new { token = order.Token });
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        return (await start.Content.ReadFromJsonAsync<RedirectDto>())!.RedirectUrl;
    }

    private async Task<OrderDto> OrderViewAsync(Shop shop, Guid id) =>
        (await (await GetAsync(api.PanelClient(), shop.Owner, $"/api/painel/pedidos/{id}")).Content.ReadFromJsonAsync<OrderDto>())!;

    private async Task<OrderDto> WaitForPaidAsync(Shop shop, Guid id)
    {
        var watch = Stopwatch.StartNew();
        OrderDto view;
        while ((view = await OrderViewAsync(shop, id)).Status != "paid")
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) Assert.Fail($"Pedido ainda {view.Status}.");
            await Task.Delay(100);
        }
        return view;
    }

    /// <summary>Notificação no formato do Mercado Pago: <c>?data.id=&amp;type=payment</c>, <c>x-request-id</c> e <c>x-signature</c>.</summary>
    private Task<HttpResponseMessage> NotifyMercadoPagoAsync(Guid tenantId, long paymentId, string secret)
    {
        var id = paymentId.ToString(CultureInfo.InvariantCulture);
        var requestId = Guid.NewGuid().ToString();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/pagamentos/mercado-pago/{tenantId}?data.id={id}&type=payment")
        {
            Content = JsonContent.Create(new { action = "payment.updated", type = "payment", data = new { id } }),
        };
        request.Headers.Add("x-request-id", requestId);
        request.Headers.Add("x-signature", $"ts={ts},v1={MercadoPagoStub.Sign(secret, id, requestId, ts)}");
        return api.PanelClient().SendAsync(request);
    }

    private Task<HttpResponseMessage> NotifyCieloAsync(Guid tenantId, string body, string contentType) =>
        api.PanelClient().PostAsync($"/api/webhooks/pagamentos/cielo/{tenantId}", new StringContent(body, Encoding.UTF8, contentType));

    private static string CieloOrder(string status, long orderNumber, long cents) => $$"""
        { "orderNumber": "PED{{orderNumber}}", "cart": { "items": [ { "name": "Peça", "unitPrice": {{cents}}, "quantity": 1, "type": "Asset" } ] },
          "shipping": { "type": "WithoutShipping", "services": [] }, "payment": { "status": "{{status}}", "type": "Pix" } }
        """;

    [Fact]
    public async Task Dono_cadastra_e_remove_as_credenciais_que_nunca_voltam_pela_API()
    {
        var shop = await ShopAsync();
        var account = NewMercadoPago();

        var empty = await SettingsAsync(shop.Owner);
        Assert.Equal(["cielo", "mercado-pago"], empty.Keys.Order());
        Assert.All(empty.Values, s => Assert.False(s.Configured || s.Active));
        Assert.Equal($"https://{shop.Tenant.Slug}.plataforma.test/api/webhooks/pagamentos/mercado-pago/{shop.Tenant.Id}", empty["mercado-pago"].NotificationUrl);
        Assert.Equal($"https://{shop.Tenant.Slug}.plataforma.test/api/webhooks/pagamentos/cielo/{shop.Tenant.Id}", empty["cielo"].NotificationUrl);

        var saved = await SaveAsync(shop.Owner, "mercado-pago", new { accessToken = $"  {account.AccessToken} ", account.WebhookSecret });
        var body = await saved.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.DoesNotContain(account.AccessToken, body, StringComparison.Ordinal);
        Assert.DoesNotContain(account.WebhookSecret, body, StringComparison.Ordinal);

        var settings = (await SettingsAsync(shop.Owner))["mercado-pago"];
        Assert.True(settings.Configured && settings.Active);
        Assert.Equal([("accessToken", account.AccessToken[^4..]), ("webhookSecret", account.WebhookSecret[^4..])],
            settings.Credentials.Select(c => (c.Field, c.Hint)));
        Assert.NotNull(settings.UpdatedAt);

        var vault = await (await GetAsync(api.PanelClient(), shop.Owner, "/api/painel/cofre")).Content.ReadAsStringAsync();
        Assert.Contains(PaymentGatewayResolver.MercadoPagoAccessTokenName, vault, StringComparison.Ordinal);
        Assert.DoesNotContain(account.AccessToken, vault, StringComparison.Ordinal);
        Assert.DoesNotContain(account.AccessToken, api.Logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(account.WebhookSecret, api.Logs.AllText, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Delete, $"{Path}/mercado-pago")).StatusCode);
        Assert.False((await SettingsAsync(shop.Owner))["mercado-pago"].Configured);
        var secrets = await (await GetAsync(api.PanelClient(), shop.Owner, "/api/painel/cofre")).Content.ReadFromJsonAsync<List<SecretDto>>();
        Assert.Empty(secrets!);
    }

    public static TheoryData<string, object> InvalidCredentials => new()
    {
        { "mercado-pago", new { accessToken = (string?)null, webhookSecret = "segredo-com-tamanho-suficiente" } },
        { "mercado-pago", new { accessToken = "curto", webhookSecret = "segredo-com-tamanho-suficiente" } },
        { "mercado-pago", new { accessToken = "APP_USR-com espaco-no-meio-do-token", webhookSecret = "segredo-com-tamanho-suficiente" } },
        { "mercado-pago", new { accessToken = "APP_USR-0000000000000000-000000-token", webhookSecret = "" } },
        { "cielo", new { merchantId = "nao-e-um-guid-mas-e-comprido", clientId = "client-id-comprido-o-bastante", clientSecret = "client-secret-comprido-o-bastante" } },
        { "cielo", new { merchantId = Guid.NewGuid().ToString(), clientId = "client-id-comprido-o-bastante" } },
    };

    [Theory]
    [MemberData(nameof(InvalidCredentials))]
    public async Task Credencial_vazia_ou_mal_formada_e_recusada_sem_gravar_nada(string gateway, object credentials)
    {
        var shop = await ShopAsync();

        var response = await SaveAsync(shop.Owner, gateway, credentials);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.All((await SettingsAsync(shop.Owner)).Values, s => Assert.Empty(s.Credentials));
    }

    [Fact]
    public async Task Gateway_desconhecido_nao_existe()
    {
        var shop = await ShopAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await SaveAsync(shop.Owner, "pagseguro", new { token = Random("x") })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Delete, $"{Path}/pagseguro")).StatusCode);
    }

    [Fact]
    public async Task So_quem_administra_o_cofre_ve_ou_muda_as_credenciais()
    {
        var shop = await ConnectAsync(await ShopAsync(), NewMercadoPago());
        var operatorSession = await api.SessionForAsync(shop.Tenant, TenantRole.Operator);

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(api.PanelClient(), operatorSession, Path)).StatusCode);
        var other = NewMercadoPago();
        Assert.Equal(HttpStatusCode.Forbidden, (await SaveAsync(operatorSession, "mercado-pago", new { other.AccessToken, other.WebhookSecret })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(api.PanelClient(), operatorSession, HttpMethod.Delete, $"{Path}/mercado-pago")).StatusCode);
        Assert.True((await SettingsAsync(shop.Owner))["mercado-pago"].Configured);
    }

    [Fact]
    public async Task Loja_com_Mercado_Pago_cobra_pelo_Checkout_Pro_e_o_pedido_vira_pago_pela_notificacao()
    {
        var account = NewMercadoPago();
        var shop = await ConnectAsync(await ShopAsync(), account);
        var order = await OrderAsync(shop, 210m);

        Assert.StartsWith(MercadoPagoStub.CheckoutUrl, await PayAsync(shop, order), StringComparison.Ordinal);

        // A preferência foi criada com o token desta loja e aponta para o pedido e para o webhook dela.
        var (token, body) = api.MercadoPago.Preferences.Single(p => p.Body.Contains(order.Id.ToString(), StringComparison.Ordinal));
        Assert.Equal(account.AccessToken, token);
        var preference = JsonDocument.Parse(body).RootElement;
        Assert.Equal(order.Id.ToString(), preference.GetProperty("external_reference").GetString());
        Assert.Equal($"http://{shop.Tenant.Slug}.plataforma.test/api/webhooks/pagamentos/mercado-pago/{shop.Tenant.Id}",
            preference.GetProperty("notification_url").GetString());
        Assert.Equal($"http://{shop.Tenant.Slug}.plataforma.test/pedido", preference.GetProperty("back_urls").GetProperty("success").GetString());

        // O comprador paga no Mercado Pago, que avisa a loja.
        var paymentId = Interlocked.Increment(ref _paymentId);
        api.MercadoPago.SetPayment(paymentId, $$"""
            { "id": {{paymentId}}, "status": "approved", "status_detail": "accredited", "transaction_amount": "210.00",
              "external_reference": "{{order.Id}}", "payment_type_id": "bank_transfer", "currency_id": "BRL" }
            """);
        Assert.Equal(HttpStatusCode.OK, (await NotifyMercadoPagoAsync(shop.Tenant.Id, paymentId, account.WebhookSecret)).StatusCode);

        var payment = Assert.Single((await WaitForPaidAsync(shop, order.Id)).Payments);
        Assert.Equal(("mercado-pago", paymentId.ToString(CultureInfo.InvariantCulture), "approved", 210m),
            (payment.Gateway, payment.ExternalId, payment.Status, payment.Amount));
        Assert.DoesNotContain(account.AccessToken, api.Logs.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Notificacao_do_Mercado_Pago_assinada_com_a_chave_de_outra_loja_e_recusada()
    {
        var account = NewMercadoPago();
        var shop = await ConnectAsync(await ShopAsync(), account);
        var other = NewMercadoPago();
        await ConnectAsync(await ShopAsync(), other);
        var order = await OrderAsync(shop, 90m);
        var paymentId = Interlocked.Increment(ref _paymentId);
        api.MercadoPago.SetPayment(paymentId, $$"""
            { "id": {{paymentId}}, "status": "approved", "transaction_amount": 90, "external_reference": "{{order.Id}}", "currency_id": "BRL" }
            """);

        Assert.Equal(HttpStatusCode.Unauthorized, (await NotifyMercadoPagoAsync(shop.Tenant.Id, paymentId, other.WebhookSecret)).StatusCode);
        // Loja sem Mercado Pago configurado não tem esse webhook.
        var plain = await ShopAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await NotifyMercadoPagoAsync(plain.Tenant.Id, paymentId, account.WebhookSecret)).StatusCode);

        await Task.Delay(300);
        Assert.Equal("pendingPayment", (await OrderViewAsync(shop, order.Id)).Status);
    }

    [Fact]
    public async Task Loja_com_Cielo_cobra_pelo_Checkout_Cielo_e_o_pedido_vira_pago_pela_consulta()
    {
        var account = NewCielo();
        var shop = await ConnectAsync(await ShopAsync(), account);
        var order = await OrderAsync(shop, 155.9m);

        Assert.StartsWith(CieloStub.CheckoutUrl, await PayAsync(shop, order), StringComparison.Ordinal);
        var (merchantId, body) = api.Cielo.Checkouts.Single(c => c.Body.Contains($"\"PED{order.Number}\"", StringComparison.Ordinal) && c.MerchantId == account.MerchantId);
        var checkout = JsonDocument.Parse(body).RootElement;
        Assert.Equal(15590L, checkout.GetProperty("Cart").GetProperty("Items")[0].GetProperty("UnitPrice").GetInt64());
        Assert.Equal($"http://{shop.Tenant.Slug}.plataforma.test/pedido", checkout.GetProperty("Options").GetProperty("ReturnUrl").GetString());

        // A Cielo avisa em formulário; só a consulta autenticada com as credenciais da loja paga o pedido.
        var checkoutId = Guid.NewGuid().ToString("N");
        api.Cielo.SetOrder(checkoutId, CieloOrder("Paid", order.Number, 15590));
        var notified = await NotifyCieloAsync(shop.Tenant.Id,
            $"checkout_cielo_order_number={checkoutId}&amount=15590&order_number=PED{order.Number}&payment_status=2&payment_method_type=6",
            "application/x-www-form-urlencoded");
        Assert.Equal(HttpStatusCode.OK, notified.StatusCode);

        var payment = Assert.Single((await WaitForPaidAsync(shop, order.Id)).Payments);
        Assert.Equal(("cielo", checkoutId, "approved", 155.9m), (payment.Gateway, payment.ExternalId, payment.Status, payment.Amount));
        Assert.Contains($"{account.ClientId}:{account.ClientSecret}", api.Cielo.TokenRequests);
        Assert.DoesNotContain(account.ClientSecret, api.Logs.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Notificacao_JSON_da_Cielo_com_o_MerchantId_da_loja_paga_pelo_numero_do_pedido()
    {
        var account = NewCielo();
        var shop = await ConnectAsync(await ShopAsync(), account);
        var order = await OrderAsync(shop, 80m);
        var paid = Guid.NewGuid().ToString("N");
        var denied = Guid.NewGuid().ToString("N");
        api.Cielo.SetOrder(paid, CieloOrder("Paid", order.Number, 8000));
        api.Cielo.SetOrder(denied, CieloOrder("Denied", order.Number, 8000));
        api.Cielo.SetOrderNumber($"PED{order.Number}", $$"""
            [ { "checkoutOrderNumber": "{{denied}}", "createdDate": "2026-10-11T14:40:00.000" },
              { "checkoutOrderNumber": "{{paid}}", "createdDate": "2026-10-11T14:30:00.000" } ]
            """);

        var forged = await NotifyCieloAsync(shop.Tenant.Id,
            $$"""{ "URL": "https://x", "MerchantId": "{{Guid.NewGuid()}}", "MerchantOrderNumber": "PED{{order.Number}}" }""", "application/json");
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);

        var notified = await NotifyCieloAsync(shop.Tenant.Id,
            $$"""{ "URL": "https://x", "MerchantId": "{{account.MerchantId}}", "MerchantOrderNumber": "PED{{order.Number}}" }""", "application/json");
        Assert.Equal(HttpStatusCode.OK, notified.StatusCode);

        var payment = Assert.Single((await WaitForPaidAsync(shop, order.Id)).Payments);
        Assert.Equal(("cielo", paid, "approved"), (payment.Gateway, payment.ExternalId, payment.Status));
    }

    [Fact]
    public async Task Aviso_falso_da_Cielo_nao_paga_o_pedido()
    {
        var shop = await ConnectAsync(await ShopAsync(), NewCielo());
        var order = await OrderAsync(shop, 60m);

        // Transação que a Cielo não conhece (a consulta falha) e transação de outro pedido não pagam este.
        var unknown = await NotifyCieloAsync(shop.Tenant.Id, $"checkout_cielo_order_number={Guid.NewGuid():N}&payment_status=2", "application/x-www-form-urlencoded");
        var otherOrder = Guid.NewGuid().ToString("N");
        api.Cielo.SetOrder(otherOrder, CieloOrder("Paid", order.Number + 1000, 6000));
        var mismatch = await NotifyCieloAsync(shop.Tenant.Id, $"checkout_cielo_order_number={otherOrder}&payment_status=2", "application/x-www-form-urlencoded");

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (unknown.StatusCode, mismatch.StatusCode));
        await Task.Delay(500);
        var view = await OrderViewAsync(shop, order.Id);
        Assert.Equal("pendingPayment", view.Status);
        Assert.Empty(view.Payments);
    }

    [Fact]
    public async Task Com_os_dois_conectados_cobra_pelo_salvo_por_ultimo_e_os_dois_webhooks_continuam()
    {
        var mercadoPago = NewMercadoPago();
        var shop = await ConnectAsync(await ConnectAsync(await ShopAsync(), mercadoPago), NewCielo());

        var settings = await SettingsAsync(shop.Owner);
        Assert.True(settings["cielo"].Active);
        Assert.False(settings["mercado-pago"].Active);
        Assert.StartsWith(CieloStub.CheckoutUrl, await PayAsync(shop, await OrderAsync(shop, 30m)), StringComparison.Ordinal);

        // Pagamento iniciado antes no Mercado Pago ainda é confirmado.
        var order = await OrderAsync(shop, 45m);
        var paymentId = Interlocked.Increment(ref _paymentId);
        api.MercadoPago.SetPayment(paymentId, $$"""
            { "id": {{paymentId}}, "status": "approved", "transaction_amount": 45, "external_reference": "{{order.Id}}", "currency_id": "BRL" }
            """);
        Assert.Equal(HttpStatusCode.OK, (await NotifyMercadoPagoAsync(shop.Tenant.Id, paymentId, mercadoPago.WebhookSecret)).StatusCode);
        await WaitForPaidAsync(shop, order.Id);

        // Desconectando a Cielo, volta a cobrar pelo Mercado Pago.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Delete, $"{Path}/cielo")).StatusCode);
        Assert.True((await SettingsAsync(shop.Owner))["mercado-pago"].Active);
        Assert.StartsWith(MercadoPagoStub.CheckoutUrl, await PayAsync(shop, await OrderAsync(shop, 20m)), StringComparison.Ordinal);
    }
}
