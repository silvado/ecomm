using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Payments;
using Ecommerce.Integration.Tests.Infrastructure;
using Ecommerce.Integration.Tests.Payments;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF15 com o Mercado Pago: o Dono cola as credenciais (cofre, nunca devolvidas) e a loja passa a cobrar pelo Checkout Pro,
/// com o pedido pago só por notificação assinada com a chave da loja e consulta ao pagamento.
/// </summary>
public sealed class MercadoPagoPaymentTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private const string Path = "/api/painel/pagamentos/mercado-pago";

    private sealed record SettingsDto(bool Configured, string? AccessTokenHint, string? WebhookSecretHint, DateTimeOffset? UpdatedAt, string NotificationUrl);
    private sealed record SecretDto(string Kind, string Name, string Hint);
    private sealed record PlacedDto(long Number, decimal Total);
    private sealed record RedirectDto(string RedirectUrl);
    private sealed record SummaryDto(Guid Id, long Number);
    private sealed record PageDto(List<SummaryDto> Items);
    private sealed record PaymentDto(string Gateway, string ExternalId, string Status, decimal Amount);
    private sealed record OrderDto(Guid Id, string Status, decimal Total, DateTimeOffset? PaidAt, List<PaymentDto> Payments);

    private sealed record Shop(Tenant Tenant, string Owner, HttpClient Store, string Token, string Secret);

    private static int _ip;
    private static long _paymentId = 9_000_000_000 + RandomNumberGenerator.GetInt32(1_000_000);

    private static string Random(string prefix) => prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));

    private async Task<Shop> ShopAsync(bool withMercadoPago = true)
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        await api.ConfigureShippingAsync(owner, tenant, "01310-100", pickup: true, "Balcão");
        var shop = new Shop(tenant, owner, api.StoreClient(tenant, $"198.51.102.{Interlocked.Increment(ref _ip) % 250 + 1}"),
            Random("APP_USR-"), Random(""));
        if (withMercadoPago)
            Assert.Equal(HttpStatusCode.OK, (await SaveAsync(shop.Owner, shop.Token, shop.Secret)).StatusCode);
        return shop;
    }

    private Task<HttpResponseMessage> SaveAsync(string session, string? token, string? secret) =>
        SendAsync(api.PanelClient(), session, HttpMethod.Put, Path, new { accessToken = token, webhookSecret = secret });

    private async Task<SettingsDto> SettingsAsync(string session) =>
        (await (await GetAsync(api.PanelClient(), session, Path)).Content.ReadFromJsonAsync<SettingsDto>())!;

    private async Task<(Guid Id, string Token, decimal Total)> OrderAsync(Shop shop, decimal price)
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
        return (page.Items.Single(o => o.Number == number).Id, token, price);
    }

    private async Task<OrderDto> OrderViewAsync(Shop shop, Guid id) =>
        (await (await GetAsync(api.PanelClient(), shop.Owner, $"/api/painel/pedidos/{id}")).Content.ReadFromJsonAsync<OrderDto>())!;

    /// <summary>Notificação no formato do Mercado Pago: <c>?data.id=&amp;type=payment</c>, <c>x-request-id</c> e <c>x-signature</c>.</summary>
    private Task<HttpResponseMessage> NotifyAsync(Guid tenantId, long paymentId, string secret)
    {
        var requestId = Guid.NewGuid().ToString();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/webhooks/pagamentos/{MercadoPagoGateway.GatewayName}/{tenantId}?data.id={paymentId}&type=payment")
        {
            Content = JsonContent.Create(new { action = "payment.updated", type = "payment", data = new { id = paymentId.ToString(System.Globalization.CultureInfo.InvariantCulture) } }),
        };
        request.Headers.Add("x-request-id", requestId);
        request.Headers.Add("x-signature", $"ts={ts},v1={MercadoPagoStub.Sign(secret, paymentId.ToString(System.Globalization.CultureInfo.InvariantCulture), requestId, ts)}");
        return api.PanelClient().SendAsync(request);
    }

    [Fact]
    public async Task Dono_cadastra_e_remove_as_credenciais_que_nunca_voltam_pela_API()
    {
        var shop = await ShopAsync(withMercadoPago: false);

        var empty = await SettingsAsync(shop.Owner);
        Assert.False(empty.Configured);
        Assert.Equal($"https://{shop.Tenant.Slug}.plataforma.test/api/webhooks/pagamentos/mercado-pago/{shop.Tenant.Id}", empty.NotificationUrl);

        var saved = await SaveAsync(shop.Owner, $"  {shop.Token} ", shop.Secret);
        var body = await saved.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.DoesNotContain(shop.Token, body, StringComparison.Ordinal);
        Assert.DoesNotContain(shop.Secret, body, StringComparison.Ordinal);

        var settings = await SettingsAsync(shop.Owner);
        Assert.True(settings.Configured);
        Assert.Equal((shop.Token[^4..], shop.Secret[^4..]), (settings.AccessTokenHint, settings.WebhookSecretHint));
        Assert.NotNull(settings.UpdatedAt);

        var vault = await (await GetAsync(api.PanelClient(), shop.Owner, "/api/painel/cofre")).Content.ReadAsStringAsync();
        Assert.Contains(PaymentGatewayResolver.MercadoPagoAccessTokenName, vault, StringComparison.Ordinal);
        Assert.DoesNotContain(shop.Token, vault, StringComparison.Ordinal);
        Assert.DoesNotContain(shop.Token, api.Logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(shop.Secret, api.Logs.AllText, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Delete, Path)).StatusCode);
        var removed = await SettingsAsync(shop.Owner);
        Assert.False(removed.Configured);
        Assert.Null(removed.AccessTokenHint);
        var secrets = await (await GetAsync(api.PanelClient(), shop.Owner, "/api/painel/cofre")).Content.ReadFromJsonAsync<List<SecretDto>>();
        Assert.Empty(secrets!);
    }

    [Theory]
    [InlineData(null, "segredo-com-tamanho-suficiente")]
    [InlineData("curto", "segredo-com-tamanho-suficiente")]
    [InlineData("APP_USR-com espaco-no-meio-do-token", "segredo-com-tamanho-suficiente")]
    [InlineData("APP_USR-0000000000000000-000000-token", null)]
    [InlineData("APP_USR-0000000000000000-000000-token", "")]
    public async Task Credencial_vazia_ou_mal_formada_e_recusada_sem_gravar_nada(string? token, string? secret)
    {
        var shop = await ShopAsync(withMercadoPago: false);

        var response = await SaveAsync(shop.Owner, token, secret);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False((await SettingsAsync(shop.Owner)).Configured);
    }

    [Fact]
    public async Task So_quem_administra_o_cofre_ve_ou_muda_as_credenciais()
    {
        var shop = await ShopAsync();
        var operatorSession = await api.SessionForAsync(shop.Tenant, TenantRole.Operator);

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(api.PanelClient(), operatorSession, Path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SaveAsync(operatorSession, Random("APP_USR-"), Random(""))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(api.PanelClient(), operatorSession, HttpMethod.Delete, Path)).StatusCode);
        Assert.True((await SettingsAsync(shop.Owner)).Configured);
    }

    [Fact]
    public async Task Loja_com_Mercado_Pago_cobra_pelo_Checkout_Pro_e_o_pedido_vira_pago_pela_notificacao()
    {
        var shop = await ShopAsync();
        var order = await OrderAsync(shop, 210m);

        var start = await shop.Store.PostAsJsonAsync("/api/loja/pedidos/pagamento", new { token = order.Token });
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.StartsWith(MercadoPagoStub.CheckoutUrl, (await start.Content.ReadFromJsonAsync<RedirectDto>())!.RedirectUrl, StringComparison.Ordinal);

        // A preferência foi criada com o token desta loja e aponta para o pedido e para o webhook dela.
        var (token, body) = api.MercadoPago.Preferences.Single(p => p.Body.Contains(order.Id.ToString(), StringComparison.Ordinal));
        Assert.Equal(shop.Token, token);
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
        Assert.Equal(HttpStatusCode.OK, (await NotifyAsync(shop.Tenant.Id, paymentId, shop.Secret)).StatusCode);

        var watch = Stopwatch.StartNew();
        OrderDto view;
        while ((view = await OrderViewAsync(shop, order.Id)).Status != "paid")
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) Assert.Fail($"Pedido ainda {view.Status}.");
            await Task.Delay(100);
        }
        var payment = Assert.Single(view.Payments);
        Assert.Equal(("mercado-pago", paymentId.ToString(System.Globalization.CultureInfo.InvariantCulture), "approved", 210m),
            (payment.Gateway, payment.ExternalId, payment.Status, payment.Amount));
        Assert.DoesNotContain(shop.Token, api.Logs.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Notificacao_assinada_com_a_chave_de_outra_loja_e_recusada()
    {
        var shop = await ShopAsync();
        var other = await ShopAsync();
        var order = await OrderAsync(shop, 90m);
        var paymentId = Interlocked.Increment(ref _paymentId);
        api.MercadoPago.SetPayment(paymentId, $$"""
            { "id": {{paymentId}}, "status": "approved", "transaction_amount": 90, "external_reference": "{{order.Id}}", "currency_id": "BRL" }
            """);

        Assert.Equal(HttpStatusCode.Unauthorized, (await NotifyAsync(shop.Tenant.Id, paymentId, other.Secret)).StatusCode);
        // Loja sem Mercado Pago configurado não tem esse webhook.
        var plain = await ShopAsync(withMercadoPago: false);
        Assert.Equal(HttpStatusCode.NotFound, (await NotifyAsync(plain.Tenant.Id, paymentId, shop.Secret)).StatusCode);

        await Task.Delay(300);
        Assert.Equal("pendingPayment", (await OrderViewAsync(shop, order.Id)).Status);
    }
}
