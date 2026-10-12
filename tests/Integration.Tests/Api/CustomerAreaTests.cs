using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Email;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Ecommerce.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF16 — área do cliente: acesso só por link no e-mail, sessão de 30 dias, o comprador vê só os pedidos do próprio e-mail
/// nesta loja; e-mails do pedido (recebido, pago, enviado, cancelado) pela fila, com novas tentativas e log.
/// </summary>
public sealed partial class CustomerAreaTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record PlacedDto(long Number, decimal Total);
    private sealed record QuoteOptionDto(string ServiceId, decimal Price);
    private sealed record QuoteDto(List<QuoteOptionDto> Options);
    private sealed record SessionDto(string Email);
    private sealed record SummaryDto(long Number, string Status, int ItemCount, decimal Total);
    private sealed record OrdersDto(string Email, List<SummaryDto> Orders);
    private sealed record StoreOrderDto(long Number, string Status, string? TrackingCode);
    private sealed record EmailSettingsDto(string? ContactEmail, bool UsesOwnSmtp, string? Host, bool HasPassword, string PlatformSender);
    private sealed record EmailTestDto(string Outcome, string? Message);
    private sealed record LogDto(string Channel, string Operation, bool Success, string? Reference);
    private sealed record LogPageDto(List<LogDto> Items);
    private sealed record ProblemDto(string Title);

    private sealed record Shop(Tenant Tenant, string Owner, HttpClient Store);

    private static int _ip;

    [GeneratedRegex("#([A-Za-z0-9_-]{43})")]
    private static partial Regex LinkToken();

    private InMemoryEmailOutbox Outbox => api.Services.GetRequiredService<InMemoryEmailOutbox>();

    private async Task<Shop> ShopAsync()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        await api.ConfigureShippingAsync(owner, tenant, "01310-100", pickup: true, "Balcão da loja");
        return new Shop(tenant, owner, api.StoreClient(tenant, $"198.51.104.{Interlocked.Increment(ref _ip) % 250 + 1}"));
    }

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string NewEmail() => $"c{Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant()}@comprador.test";

    private async Task<(Guid Id, long Number, string Token)> OrderAsync(Shop shop, string email, decimal price = 100m, bool pickup = true)
    {
        var part = await api.CreatePartAsync(shop.Owner, price);
        var token = NewToken();
        var items = new[] { new { partId = part, quantity = 1 } };
        var freight = 0m;
        if (!pickup)
        {
            var quote = await shop.Store.PostAsJsonAsync("/api/loja/frete", new { cep = "88015-200", items });
            freight = (await quote.Content.ReadFromJsonAsync<QuoteDto>())!.Options.Single(o => o.ServiceId == "fake-economico").Price;
        }
        var response = await shop.Store.PostAsJsonAsync("/api/loja/pedidos", new
        {
            token, expectedTotal = price + freight, buyer = TestBuyers.Buyer() with { Email = email.ToUpperInvariant() }, items,
            delivery = pickup ? (object)new { method = "pickup" } : new { method = "shipping", address = TestBuyers.Address(), serviceId = "fake-economico" },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var number = (await response.Content.ReadFromJsonAsync<PlacedDto>())!.Number;
        var id = await InTenantAsync(shop.Tenant.Id, db => db.CustomerOrders.Where(o => o.Number == number).Select(o => o.Id).SingleAsync());
        return (id, number, token);
    }

    private async Task<T> InTenantAsync<T>(Guid tenantId, Func<TenantDbContext, Task<T>> query)
    {
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        return await query(scope.ServiceProvider.GetRequiredService<TenantDbContext>());
    }

    /// <summary>Os e-mails saem pela fila: espera até aparecer o que se procura.</summary>
    private async Task<SentEmail> WaitForEmailAsync(Func<SentEmail, bool> match)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (Outbox.Sent.FirstOrDefault(match) is { } email) return email;
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) Assert.Fail("E-mail esperado não foi enviado.");
            await Task.Delay(100);
        }
    }

    private async Task<string> RequestLinkAsync(Shop shop, string email)
    {
        var response = await shop.Store.PostAsJsonAsync("/api/loja/cliente/acesso", new { email });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var sent = await WaitForEmailAsync(e => e.TenantId == shop.Tenant.Id && e.Purpose == "link de acesso" && string.Equals(e.Message.To, email, StringComparison.OrdinalIgnoreCase));
        return LinkToken().Match(sent.Message.Text).Groups[1].Value;
    }

    /// <summary>Abre a sessão com o link e devolve o cookie (o cliente de teste não guarda cookies: Secure em http).</summary>
    private static async Task<string> StartSessionAsync(Shop shop, string linkToken)
    {
        var response = await shop.Store.PostAsJsonAsync("/api/loja/cliente/sessao", new { token = linkToken });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/loja/cliente", cookie, StringComparison.OrdinalIgnoreCase);
        return cookie.Split(';')[0];
    }

    private static Task<HttpResponseMessage> CustomerGetAsync(Shop shop, string cookie, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", cookie);
        return shop.Store.SendAsync(request);
    }

    [Fact]
    public async Task Pedido_recebido_manda_email_com_o_link_do_pedido_e_registra_no_log_sem_o_email_inteiro()
    {
        var shop = await ShopAsync();
        var email = NewEmail();
        var (id, number, token) = await OrderAsync(shop, email);

        var sent = await WaitForEmailAsync(e => e.OrderId == id && e.Purpose == "pedido recebido");
        Assert.Equal(shop.Tenant.Id, sent.TenantId);
        Assert.Equal(email, sent.Message.To);
        Assert.Contains($"Pedido nº {number} recebido", sent.Message.Subject, StringComparison.Ordinal);
        Assert.Contains(shop.Tenant.TradeName, sent.Message.Subject, StringComparison.Ordinal);
        Assert.Contains($"https://{shop.Tenant.Slug}.plataforma.test/pedido#{token}", sent.Message.Text, StringComparison.Ordinal);
        Assert.Contains("R$ 100,00", sent.Message.Html, StringComparison.Ordinal);

        var log = (await (await GetAsync(api.PanelClient(), shop.Owner, $"/api/painel/integracoes/log?pedido={id}")).Content.ReadFromJsonAsync<LogPageDto>())!;
        Assert.Contains(log.Items, l => l.Operation == "enviar e-mail (pedido recebido)" && l.Success);
        Assert.DoesNotContain(log.Items, l => l.Reference?.Contains(email, StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Comprador_entra_pelo_link_e_ve_so_os_proprios_pedidos_desta_loja()
    {
        var shop = await ShopAsync();
        var other = await ShopAsync();
        var email = NewEmail();
        var (_, mine, _) = await OrderAsync(shop, email, 120m);
        var (_, someoneElse, _) = await OrderAsync(shop, NewEmail());
        await OrderAsync(other, email); // mesmo e-mail, outra loja

        var cookie = await StartSessionAsync(shop, await RequestLinkAsync(shop, email.ToUpperInvariant()));

        var orders = (await (await CustomerGetAsync(shop, cookie, "/api/loja/cliente/pedidos")).Content.ReadFromJsonAsync<OrdersDto>())!;
        Assert.Equal(email, orders.Email);
        var summary = Assert.Single(orders.Orders);
        Assert.Equal((mine, "pendingPayment", 1, 120m), (summary.Number, summary.Status, summary.ItemCount, summary.Total));

        var detail = await CustomerGetAsync(shop, cookie, $"/api/loja/cliente/pedidos/{mine}");
        Assert.Equal(mine, (await detail.Content.ReadFromJsonAsync<StoreOrderDto>())!.Number);
        Assert.Equal("no-store", detail.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await CustomerGetAsync(shop, cookie, $"/api/loja/cliente/pedidos/{someoneElse}")).StatusCode);

        // A sessão é da loja: o mesmo cookie não abre nada na outra.
        Assert.Equal(HttpStatusCode.Unauthorized, (await CustomerGetAsync(other, cookie, "/api/loja/cliente/pedidos")).StatusCode);

        var logout = new HttpRequestMessage(HttpMethod.Post, "/api/loja/cliente/sair");
        logout.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.NoContent, (await shop.Store.SendAsync(logout)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CustomerGetAsync(shop, cookie, "/api/loja/cliente/pedidos")).StatusCode);
    }

    [Fact]
    public async Task Link_vale_uma_vez_por_15_minutos_e_link_falso_tem_a_mesma_resposta()
    {
        var shop = await ShopAsync();
        var email = NewEmail();
        await OrderAsync(shop, email);
        var link = await RequestLinkAsync(shop, email);

        await StartSessionAsync(shop, link);
        var reused = await shop.Store.PostAsJsonAsync("/api/loja/cliente/sessao", new { token = link });
        var fake = await shop.Store.PostAsJsonAsync("/api/loja/cliente/sessao", new { token = NewToken() });
        var malformed = await shop.Store.PostAsJsonAsync("/api/loja/cliente/sessao", new { token = "abc" });
        foreach (var response in new[] { reused, fake, malformed })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }
        Assert.Equal((await reused.Content.ReadFromJsonAsync<ProblemDto>())!.Title, (await fake.Content.ReadFromJsonAsync<ProblemDto>())!.Title);

        var expired = await RequestLinkAsync(shop, email);
        await InTenantAsync(shop.Tenant.Id, db => db.Database.ExecuteSqlAsync($"UPDATE customer_access_links SET expires_at = now() - interval '1 second'"));
        Assert.Equal(HttpStatusCode.BadRequest, (await shop.Store.PostAsJsonAsync("/api/loja/cliente/sessao", new { token = expired })).StatusCode);

        // O banco guarda só o hash.
        Assert.False(await InTenantAsync(shop.Tenant.Id, db => db.CustomerAccessLinks.AnyAsync(l => l.TokenHash.Length != 32)));
    }

    [Fact]
    public async Task Pedir_link_nao_revela_quem_comprou_e_tem_limite_por_email()
    {
        var shop = await ShopAsync();
        var buyer = NewEmail();
        await OrderAsync(shop, buyer);
        var stranger = NewEmail();

        var unknown = await shop.Store.PostAsJsonAsync("/api/loja/cliente/acesso", new { email = stranger });
        var invalid = await shop.Store.PostAsJsonAsync("/api/loja/cliente/acesso", new { email = "não é e-mail" });
        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, invalid.StatusCode);

        // Limite por e-mail (cada IP novo para não esbarrar no limite por IP).
        for (var i = 0; i < 7; i++)
        {
            var client = api.StoreClient(shop.Tenant, $"198.51.105.{Interlocked.Increment(ref _ip) % 250 + 1}");
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/loja/cliente/acesso", new { email = buyer })).StatusCode);
        }
        await WaitForEmailAsync(e => e.Message.To == buyer && e.Purpose == "link de acesso");
        await Task.Delay(500);
        Assert.Equal(5, Outbox.Sent.Count(e => e.Message.To == buyer && e.Purpose == "link de acesso"));
        Assert.DoesNotContain(Outbox.Sent, e => e.Message.To == stranger);
    }

    [Fact]
    public async Task Limite_por_ip_para_pedir_link()
    {
        var shop = await ShopAsync();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++)
            statuses.Add((await shop.Store.PostAsJsonAsync("/api/loja/cliente/acesso", new { email = NewEmail() })).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }

    [Fact]
    public async Task Envio_e_cancelamento_pela_loja_avisam_o_comprador()
    {
        var shop = await ShopAsync();
        var email = NewEmail();
        var shipped = await OrderAsync(shop, email, pickup: false);
        var canceled = await OrderAsync(shop, email);
        // Pago sem passar pelo gateway (o pagamento tem os próprios testes).
        await InTenantAsync(shop.Tenant.Id, db => db.Database.ExecuteSqlAsync($"UPDATE customer_orders SET status = 'Paid', paid_at = now() WHERE id = {shipped.Id}"));

        Assert.True((await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Post, $"/api/painel/pedidos/{shipped.Id}/enviar", new { rastreio = "AA123456789BR" })).IsSuccessStatusCode);
        Assert.True((await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Post, $"/api/painel/pedidos/{canceled.Id}/cancelar", new { motivo = "Peça avariada <b>no estoque</b>" })).IsSuccessStatusCode);

        var shipMail = await WaitForEmailAsync(e => e.OrderId == shipped.Id && e.Purpose == "pedido enviado");
        Assert.Contains("AA123456789BR", shipMail.Message.Text, StringComparison.Ordinal);
        Assert.Contains($"https://{shop.Tenant.Slug}.plataforma.test/meus-pedidos", shipMail.Message.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(shipped.Token, shipMail.Message.Text, StringComparison.Ordinal);

        var cancelMail = await WaitForEmailAsync(e => e.OrderId == canceled.Id && e.Purpose == "pedido cancelado");
        Assert.Contains("&lt;b&gt;no estoque&lt;/b&gt;", cancelMail.Message.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>no estoque", cancelMail.Message.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Servidor_de_email_fora_do_ar_tenta_de_novo_e_registra_a_falha_no_log()
    {
        var shop = await ShopAsync();
        Outbox.FailNext(1);
        var (id, _, _) = await OrderAsync(shop, NewEmail());

        await WaitForEmailAsync(e => e.OrderId == id && e.Purpose == "pedido recebido");
        var log = (await (await GetAsync(api.PanelClient(), shop.Owner, $"/api/painel/integracoes/log?pedido={id}")).Content.ReadFromJsonAsync<LogPageDto>())!;
        Assert.Contains(log.Items, l => l.Operation == "enviar e-mail (pedido recebido)" && !l.Success);
        Assert.Contains(log.Items, l => l.Operation == "enviar e-mail (pedido recebido)" && l.Success);
    }

    [Fact]
    public async Task Dono_configura_o_smtp_da_loja_com_senha_no_cofre_e_operador_nao_ve()
    {
        var shop = await ShopAsync();
        var initial = (await (await GetAsync(api.PanelClient(), shop.Owner, "/api/painel/loja/email")).Content.ReadFromJsonAsync<EmailSettingsDto>())!;
        Assert.False(initial.UsesOwnSmtp);

        var smtp = new { host = "smtp.gmail.com", port = 587, security = "startTls", user = "contato@loja.test", senderName = "Loja", senderAddress = "contato@loja.test" };
        var noPassword = await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Put, "/api/painel/loja/email",
            new { contactEmail = "contato@loja.test", useOwnSmtp = true, smtp, password = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, noPassword.StatusCode);

        var secret = "senha-de-app-" + NewToken()[..8];
        var saved = await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Put, "/api/painel/loja/email",
            new { contactEmail = "Contato@Loja.test", useOwnSmtp = true, smtp, password = secret });
        var body = await saved.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        var view = (await saved.Content.ReadFromJsonAsync<EmailSettingsDto>())!;
        Assert.Equal(("contato@loja.test", true, "smtp.gmail.com", true), (view.ContactEmail, view.UsesOwnSmtp, view.Host, view.HasPassword));

        // Voltar ao remetente da plataforma apaga a senha do cofre.
        var back = (await (await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Put, "/api/painel/loja/email",
            new { contactEmail = "contato@loja.test", useOwnSmtp = false })).Content.ReadFromJsonAsync<EmailSettingsDto>())!;
        Assert.False(back.HasPassword);

        var test = await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Post, "/api/painel/loja/email/teste", new { destinatario = "dono@loja.test" });
        Assert.Equal("sent", (await test.Content.ReadFromJsonAsync<EmailTestDto>())!.Outcome);
        Assert.Contains(Outbox.Sent, e => e.TenantId == shop.Tenant.Id && e.Purpose == "teste de e-mail" && e.Message.To == "dono@loja.test");

        var operatorToken = await api.SessionForAsync(shop.Tenant, TenantRole.Operator);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(api.PanelClient(), operatorToken, "/api/painel/loja/email")).StatusCode);
    }

    [Fact]
    public async Task Tabelas_da_area_do_cliente_isoladas_por_loja()
    {
        var shop = await ShopAsync();
        var other = await ShopAsync();
        var email = NewEmail();
        await OrderAsync(shop, email);
        await StartSessionAsync(shop, await RequestLinkAsync(shop, email));

        Assert.True(await InTenantAsync(shop.Tenant.Id, db => db.CustomerSessions.AnyAsync()));
        Assert.False(await InTenantAsync(other.Tenant.Id, db => db.CustomerSessions.IgnoreQueryFilters().AnyAsync()));
        Assert.False(await InTenantAsync(other.Tenant.Id, db => db.CustomerAccessLinks.IgnoreQueryFilters().AnyAsync()));
    }
}
