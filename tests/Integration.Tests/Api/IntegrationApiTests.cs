using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Ecommerce.Domain.Integrations;
using Ecommerce.Infrastructure.Integrations;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Ecommerce.Integration.Tests.Infrastructure;
using Ecommerce.Integration.Tests.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF28/RF29/RNF06 — gateway fora do ar: a fila tenta de novo, esgota, registra a falha para a loja (sem segredos) e a
/// loja reprocessa quando o gateway volta; toda chamada externa fica no log de integração, ligada ao pedido e à
/// correlação; retenção de 90 dias; métricas só na rede interna; isolamento entre lojas.
/// </summary>
public sealed class IntegrationApiTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record PlacedDto(long Number, decimal Total);
    private sealed record SummaryDto(Guid Id, long Number);
    private sealed record PageDto(List<SummaryDto> Items);
    private sealed record OrderDto(Guid Id, string Status);
    private sealed record LogDto(string Channel, string Operation, bool Success, string? ErrorSummary, string? Reference, Guid? OrderId, long? OrderNumber, string? CorrelationId);
    private sealed record LogPageDto(List<LogDto> Items, int Total);
    private sealed record FailureDto(Guid Id, string Description, int Attempts, string ErrorType, string? ErrorSummary, string Status, string? ReplayedByEmail);
    private sealed record IntegrationSummaryDto(int PendingFailures, int FailedCallsLast24h);
    private sealed record MetricsDto(int Pending, int Scheduled, int DeadLetters);

    private sealed record Shop(Guid TenantId, string Owner, HttpClient Store, string AccessToken, string WebhookSecret);

    private static int _ip;
    private static long _paymentId = 8_000_000_000 + RandomNumberGenerator.GetInt32(1_000_000);

    private static string Random(string prefix) => prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));

    private async Task<Shop> ShopAsync(bool mercadoPago = true)
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        await api.ConfigureShippingAsync(owner, tenant, "01310-100", pickup: true, "Balcão");
        var (token, secret) = (Random("APP_USR-"), Random(""));
        if (mercadoPago)
            Assert.Equal(HttpStatusCode.OK,
                (await SendAsync(api.PanelClient(), owner, HttpMethod.Put, "/api/painel/pagamentos/mercado-pago", new { accessToken = token, webhookSecret = secret })).StatusCode);
        return new Shop(tenant.Id, owner, api.StoreClient(tenant, $"198.51.105.{Interlocked.Increment(ref _ip) % 250 + 1}"), token, secret);
    }

    private async Task<(Guid Id, string Token)> OrderAsync(Shop shop, decimal price)
    {
        var part = await api.CreatePartAsync(shop.Owner, price: price, quantity: 2);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var response = await shop.Store.PostAsJsonAsync("/api/loja/pedidos", new
        {
            token, expectedTotal = price, buyer = TestBuyers.Buyer(), items = new[] { new { partId = part, quantity = 1 } }, delivery = new { method = "pickup" },
        });
        var number = (await response.Content.ReadFromJsonAsync<PlacedDto>())!.Number;
        var page = (await (await GetAsync(api.PanelClient(), shop.Owner, "/api/painel/pedidos")).Content.ReadFromJsonAsync<PageDto>())!;
        return (page.Items.Single(o => o.Number == number).Id, token);
    }

    private Task<HttpResponseMessage> NotifyAsync(Shop shop, long paymentId)
    {
        var id = paymentId.ToString(CultureInfo.InvariantCulture);
        var requestId = Guid.NewGuid().ToString();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/pagamentos/mercado-pago/{shop.TenantId}?data.id={id}&type=payment")
        {
            Content = JsonContent.Create(new { action = "payment.updated", type = "payment", data = new { id } }),
        };
        request.Headers.Add("x-request-id", requestId);
        request.Headers.Add("x-signature", $"ts={ts},v1={MercadoPagoStub.Sign(shop.WebhookSecret, id, requestId, ts)}");
        return api.PanelClient().SendAsync(request);
    }

    private static async Task<T> WaitForAsync<T>(Func<Task<T>> read, Func<T, bool> done, string what, int seconds = 90)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var value = await read();
            if (done(value)) return value;
            if (watch.Elapsed > TimeSpan.FromSeconds(seconds)) Assert.Fail($"Esperando {what}.");
            await Task.Delay(200);
        }
    }

    private async Task<List<FailureDto>> FailuresAsync(Shop shop, bool pending = true) =>
        (await (await GetAsync(api.PanelClient(), shop.Owner, $"/api/painel/integracoes/falhas?pendentes={pending}")).Content.ReadFromJsonAsync<List<FailureDto>>())!;

    private async Task<LogPageDto> LogAsync(Shop shop, string query) =>
        (await (await GetAsync(api.PanelClient(), shop.Owner, $"/api/painel/integracoes/log{query}")).Content.ReadFromJsonAsync<LogPageDto>())!;

    private async Task<T> InTenantAsync<T>(Guid tenantId, Func<TenantDbContext, Task<T>> query)
    {
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        return await query(scope.ServiceProvider.GetRequiredService<TenantDbContext>());
    }

    [Fact]
    public async Task Gateway_fora_do_ar_tenta_de_novo_vira_falha_da_loja_e_reprocessar_paga_o_pedido()
    {
        var shop = await ShopAsync();
        var (orderId, orderToken) = await OrderAsync(shop, 90m);
        Assert.Equal(HttpStatusCode.OK, (await shop.Store.PostAsJsonAsync("/api/loja/pedidos/pagamento", new { token = orderToken })).StatusCode);
        var paymentId = Interlocked.Increment(ref _paymentId);
        api.MercadoPago.SetPayment(paymentId, $$"""
            { "id": {{paymentId}}, "status": "approved", "transaction_amount": 90, "external_reference": "{{orderId}}", "currency_id": "BRL" }
            """);
        api.MercadoPago.FailPayment(paymentId, times: 100);

        Assert.Equal(HttpStatusCode.OK, (await NotifyAsync(shop, paymentId)).StatusCode);
        var failure = Assert.Single(await WaitForAsync(() => FailuresAsync(shop), f => f.Count > 0, "a falha da loja"));

        // 1 tentativa + 2 novas (configuração de teste), depois a fila de falhas.
        Assert.Equal(("Confirmar pagamento no gateway", 3, "PaymentGatewayException"), (failure.Description, failure.Attempts, failure.ErrorType));
        Assert.Contains("503", failure.ErrorSummary, StringComparison.Ordinal);
        Assert.DoesNotContain(shop.AccessToken, failure.ErrorSummary ?? "", StringComparison.Ordinal);
        Assert.Equal(3, api.MercadoPago.Lookups[paymentId.ToString(CultureInfo.InvariantCulture)]);
        var summary = await (await GetAsync(api.PanelClient(), shop.Owner, "/api/painel/integracoes/resumo")).Content.ReadFromJsonAsync<IntegrationSummaryDto>();
        Assert.Equal(1, summary!.PendingFailures);
        Assert.True(summary.FailedCallsLast24h >= 3);
        Assert.Equal("pendingPayment", (await (await GetAsync(api.PanelClient(), shop.Owner, $"/api/painel/pedidos/{orderId}")).Content.ReadFromJsonAsync<OrderDto>())!.Status);

        // O gateway volta; a loja reprocessa e o pagamento entra.
        api.MercadoPago.FailPayment(paymentId, times: 0);
        Assert.Equal(HttpStatusCode.NoContent,
            (await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Post, $"/api/painel/integracoes/falhas/{failure.Id}/reprocessar")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Post, $"/api/painel/integracoes/falhas/{failure.Id}/reprocessar")).StatusCode);
        await WaitForAsync(async () => (await (await GetAsync(api.PanelClient(), shop.Owner, $"/api/painel/pedidos/{orderId}")).Content.ReadFromJsonAsync<OrderDto>())!,
            o => o.Status == "paid", "o pedido pago depois de reprocessar");

        var replayed = Assert.Single(await FailuresAsync(shop, pending: false));
        Assert.Equal("replayed", replayed.Status);
        Assert.EndsWith("@pecas.test", replayed.ReplayedByEmail, StringComparison.Ordinal);
        Assert.Empty(await FailuresAsync(shop));

        // Log do pedido: a cobrança criada e a consulta que deu certo (ligada ao pedido depois de identificá-lo).
        var orderLog = await LogAsync(shop, $"?pedido={orderId}");
        Assert.Contains(orderLog.Items, l => l is { Channel: "mercado-pago", Operation: "criar cobrança", Success: true });
        Assert.Contains(orderLog.Items, l => l.Operation == "consultar pagamento" && l.Success && l.Reference == paymentId.ToString(CultureInfo.InvariantCulture));
        var failed = await LogAsync(shop, "?situacao=falha&canal=mercado-pago");
        Assert.True(failed.Total >= 3);
        Assert.All(failed.Items, l => Assert.DoesNotContain(shop.AccessToken, l.ErrorSummary ?? "", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cotacao_de_frete_entra_no_log_com_a_correlacao_da_requisicao()
    {
        var shop = await ShopAsync(mercadoPago: false);
        var part = await api.CreatePartAsync(shop.Owner, price: 100m, quantity: 1);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/loja/frete")
        {
            Content = JsonContent.Create(new { cep = "88015-200", items = new[] { new { partId = part, quantity = 1 } } }),
        };
        request.Headers.Add("X-Correlation-Id", "teste-correlacao-frete;<x>");

        var response = await shop.Store.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("teste-correlacao-fretex", Assert.Single(response.Headers.GetValues("X-Correlation-Id")));
        var log = Assert.Single((await LogAsync(shop, "?canal=fake")).Items);
        Assert.Equal(("cotar frete", true, "CEP 88015200", "teste-correlacao-fretex"), (log.Operation, log.Success, log.Reference, log.CorrelationId));
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(api.PanelClient(), shop.Owner, "/api/painel/integracoes/log?situacao=talvez")).StatusCode);
    }

    [Fact]
    public async Task Outra_loja_nao_ve_log_nem_falha_e_nao_reprocessa()
    {
        var mine = await ShopAsync(mercadoPago: false);
        var other = await ShopAsync(mercadoPago: false);
        var failureId = await InTenantAsync(mine.TenantId, async db =>
        {
            var failure = new MessageFailure(mine.TenantId, Guid.NewGuid(), "Teste", "Confirmar pagamento no gateway", 3, "PaymentGatewayException", "503", null, DateTimeOffset.UtcNow);
            db.MessageFailures.Add(failure);
            db.IntegrationLogs.Add(new IntegrationLogEntry(mine.TenantId, "fake", "cotar frete", true, 5, null, null, null, null, null, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
            return failure.Id;
        });

        Assert.Empty(await FailuresAsync(other));
        Assert.Equal(0, (await LogAsync(other, "")).Total);
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(api.PanelClient(), other.Owner, HttpMethod.Post, $"/api/painel/integracoes/falhas/{failureId}/reprocessar")).StatusCode);
        Assert.Single(await FailuresAsync(mine));
    }

    [Fact]
    public async Task Retencao_apaga_so_o_que_passou_de_90_dias()
    {
        var shop = await ShopAsync(mercadoPago: false);
        await InTenantAsync(shop.TenantId, async db =>
        {
            db.IntegrationLogs.Add(new IntegrationLogEntry(shop.TenantId, "fake", "antiga", true, 1, null, null, null, null, null, DateTimeOffset.UtcNow.AddDays(-91)));
            db.IntegrationLogs.Add(new IntegrationLogEntry(shop.TenantId, "fake", "recente", true, 1, null, null, null, null, null, DateTimeOffset.UtcNow.AddDays(-89)));
            return await db.SaveChangesAsync();
        });

        var retention = new IntegrationLogRetention(api.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<IntegrationLogRetention>.Instance);
        Assert.True(await retention.PurgeAsync(CancellationToken.None) >= 1);

        Assert.Equal(["recente"], await InTenantAsync(shop.TenantId, db => db.IntegrationLogs.Select(l => l.Operation).ToListAsync()));
    }

    [Fact]
    public async Task Metricas_da_fila_so_na_rede_interna()
    {
        var metrics = await api.ClientFor("api:8080", "10.0.0.2").GetFromJsonAsync<MetricsDto>("/internal/metricas");
        Assert.NotNull(metrics);
        Assert.True(metrics.Pending >= 0 && metrics.DeadLetters >= 0);
        Assert.Equal(HttpStatusCode.NotFound, (await api.ClientFor("api.plataforma.test", "203.0.113.10").GetAsync("/internal/metricas")).StatusCode);
    }
}
