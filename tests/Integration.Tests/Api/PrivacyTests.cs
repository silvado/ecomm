using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Ecommerce.Domain.Customers;
using Ecommerce.Domain.Fiscal;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Orders;
using Ecommerce.Domain.Platform;
using Ecommerce.Domain.Privacy;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Ecommerce.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RNF04 — anonimização do comprador a pedido dele (contato e endereço saem; com NF-e, nome e CPF/CNPJ ficam; pedido em
/// andamento impede) e registro de acesso a dados pessoais (somente inclusão, por loja).
/// </summary>
public sealed class PrivacyTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record PlacedDto(long Number, decimal Total);
    private sealed record QuoteOptionDto(string ServiceId, decimal Price);
    private sealed record QuoteDto(List<QuoteOptionDto> Options);
    private sealed record PreviewDto(int Orders, List<long> InProgress, int WithInvoice, int AlreadyAnonymized);
    private sealed record AccessDto(string UserEmail, string Action, Guid? OrderId, long? OrderNumber, string? Details, string? Ip);
    private sealed record AccessPageDto(List<AccessDto> Items, bool HasMore);

    private sealed record Shop(Tenant Tenant, string Owner, HttpClient Store, Guid Part);

    private static int _ip;

    private async Task<Shop> ShopAsync()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        await api.ConfigureShippingAsync(owner, tenant, "01310-100", pickup: true, "Balcão da loja");
        var part = await api.CreatePartAsync(owner, quantity: 10);
        return new Shop(tenant, owner, api.StoreClient(tenant, $"198.51.104.{Interlocked.Increment(ref _ip) % 250 + 1}"), part);
    }

    /// <summary>Pedido com entrega (endereço completo) e o id dele.</summary>
    private async Task<(Guid Id, long Number)> OrderAsync(Shop shop, string email, string cpf, string name = "Maria da Silva")
    {
        var items = new[] { new { partId = shop.Part, quantity = 1 } };
        var quote = await shop.Store.PostAsJsonAsync("/api/loja/frete", new { cep = "88015-200", items });
        var freight = (await quote.Content.ReadFromJsonAsync<QuoteDto>())!.Options.Single(o => o.ServiceId == "fake-economico").Price;
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var response = await shop.Store.PostAsJsonAsync("/api/loja/pedidos", new
        {
            token, items, expectedTotal = 100m + freight,
            buyer = TestBuyers.Buyer() with { Name = name, Email = email, Cpf = cpf },
            delivery = new { method = "shipping", address = TestBuyers.Address(), serviceId = "fake-economico" },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var number = (await response.Content.ReadFromJsonAsync<PlacedDto>())!.Number;
        return (await InTenantAsync(shop.Tenant.Id, db => db.CustomerOrders.Where(o => o.Number == number).Select(o => o.Id).SingleAsync()), number);
    }

    private async Task<T> InTenantAsync<T>(Guid tenantId, Func<TenantDbContext, Task<T>> query)
    {
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        return await query(scope.ServiceProvider.GetRequiredService<TenantDbContext>());
    }

    private Task<int> SetStatusAsync(Shop shop, Guid orderId, OrderStatus status) =>
        InTenantAsync(shop.Tenant.Id, db => db.Database.ExecuteSqlAsync($"UPDATE customer_orders SET status = {status.ToString()} WHERE id = {orderId}"));

    private Task<int> RegisterInvoiceAsync(Shop shop, Guid orderId) => InTenantAsync(shop.Tenant.Id, async db =>
    {
        var body = string.Create(CultureInfo.InvariantCulture, $"422610{shop.Tenant.Cnpj.Value}55001{RandomNumberGenerator.GetInt32(1, 999_999_999):D9}112345678");
        db.FiscalDocuments.Add(FiscalDocument.RegisterExternal(shop.Tenant.Id, orderId, body + AccessKey.CheckDigit(body).ToString(CultureInfo.InvariantCulture),
            null, null, DateTimeOffset.UtcNow));
        return await db.SaveChangesAsync();
    });

    private Task<HttpResponseMessage> PrivacyAsync(Shop shop, string path, object body, string? session = null) =>
        SendAsync(api.PanelClient(), session ?? shop.Owner, HttpMethod.Post, $"/api/painel/privacidade/{path}", body);

    [Fact]
    public async Task Anonimiza_contato_e_endereco_e_sem_nota_tambem_nome_e_CPF_sem_tocar_em_outro_comprador()
    {
        var shop = await ShopAsync();
        var cpf = TestBuyers.Cpf();
        var canceled = await OrderAsync(shop, "maria@exemplo.com.br", cpf);
        var invoiced = await OrderAsync(shop, "MARIA@exemplo.com.br", TestBuyers.Cpf());
        var other = await OrderAsync(shop, "joao@exemplo.com.br", TestBuyers.Cpf(), "João Souza");
        await SetStatusAsync(shop, canceled.Id, OrderStatus.Canceled);
        await SetStatusAsync(shop, invoiced.Id, OrderStatus.Delivered);
        await SetStatusAsync(shop, other.Id, OrderStatus.Delivered);
        await RegisterInvoiceAsync(shop, invoiced.Id);
        await InTenantAsync(shop.Tenant.Id, db =>
        {
            db.CustomerSessions.Add(new CustomerSession(shop.Tenant.Id, "maria@exemplo.com.br", RandomNumberGenerator.GetBytes(32), DateTimeOffset.UtcNow));
            db.CustomerSessions.Add(new CustomerSession(shop.Tenant.Id, "joao@exemplo.com.br", RandomNumberGenerator.GetBytes(32), DateTimeOffset.UtcNow));
            return db.SaveChangesAsync();
        });

        var preview = (await (await PrivacyAsync(shop, "titular/previa", new { email = " Maria@Exemplo.com.br " })).Content.ReadFromJsonAsync<PreviewDto>())!;
        Assert.Equal((2, 0, 1, 0), (preview.Orders, preview.InProgress.Count, preview.WithInvoice, preview.AlreadyAnonymized));
        Assert.Equal(1, (await (await PrivacyAsync(shop, "titular/previa", new { documento = $"{cpf[..3]}.{cpf[3..6]}.{cpf[6..9]}-{cpf[9..]}" })).Content.ReadFromJsonAsync<PreviewDto>())!.Orders);

        var done = await PrivacyAsync(shop, "titular/anonimizar", new { email = "maria@exemplo.com.br" });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);

        var orders = await InTenantAsync(shop.Tenant.Id, db => db.CustomerOrders.AsNoTracking().ToDictionaryAsync(o => o.Id));
        var a = orders[canceled.Id];
        Assert.Equal((CustomerOrder.AnonymizedName, "", "", "", (string?)null, (string?)null, (string?)null), (a.BuyerName, a.BuyerCpf, a.BuyerEmail, a.BuyerPhone, a.DeliveryStreet, a.DeliveryPostalCode, a.DeliveryNumber));
        Assert.Equal("Florianópolis", a.DeliveryCity);
        Assert.NotNull(a.AnonymizedAt);
        var b = orders[invoiced.Id];
        Assert.Equal(("Maria da Silva", "", "", (string?)null), (b.BuyerName, b.BuyerEmail, b.BuyerPhone, b.DeliveryStreet));
        Assert.Equal(11, b.BuyerCpf.Length);
        Assert.Equal(("João Souza", "joao@exemplo.com.br"), (orders[other.Id].BuyerName, orders[other.Id].BuyerEmail));
        Assert.Equal(["joao@exemplo.com.br"], await InTenantAsync(shop.Tenant.Id, db => db.CustomerSessions.Select(s => s.Email).ToListAsync()));

        var log = (await (await GetAsync(api.PanelClient(), shop.Owner, "/api/painel/privacidade/acessos?acao=titular.anonimizado")).Content.ReadFromJsonAsync<AccessPageDto>())!;
        var entry = Assert.Single(log.Items);
        Assert.Equal("2 pedido(s); 1 com nota fiscal mantêm nome e CPF/CNPJ", entry.Details);
        Assert.DoesNotContain("maria", entry.Details!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pedido_em_andamento_impede_e_nada_muda()
    {
        var shop = await ShopAsync();
        var open = await OrderAsync(shop, "ana@exemplo.com.br", TestBuyers.Cpf());
        var finished = await OrderAsync(shop, "ana@exemplo.com.br", TestBuyers.Cpf());
        await SetStatusAsync(shop, finished.Id, OrderStatus.Delivered);

        var preview = (await (await PrivacyAsync(shop, "titular/previa", new { email = "ana@exemplo.com.br" })).Content.ReadFromJsonAsync<PreviewDto>())!;
        Assert.Equal([open.Number], preview.InProgress);
        var refused = await PrivacyAsync(shop, "titular/anonimizar", new { email = "ana@exemplo.com.br" });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains($"nº {open.Number}", await TitleAsync(refused), StringComparison.Ordinal);
        Assert.False(await InTenantAsync(shop.Tenant.Id, db => db.CustomerOrders.AnyAsync(o => o.AnonymizedAt != null || o.BuyerEmail == "")));
        Assert.Equal(HttpStatusCode.BadRequest, (await PrivacyAsync(shop, "titular/previa", new { email = "não é e-mail" })).StatusCode);
    }

    [Fact]
    public async Task Abrir_pedido_e_baixar_nota_ficam_registrados_so_o_Dono_consulta_e_cada_loja_ve_os_seus()
    {
        var shop = await ShopAsync();
        var other = await ShopAsync();
        var order = await OrderAsync(shop, "bia@exemplo.com.br", TestBuyers.Cpf());
        var operador = await api.SessionForAsync(shop.Tenant, TenantRole.Operator);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(api.PanelClient(), operador, $"/api/painel/pedidos/{order.Id}")).StatusCode);

        var page = (await (await GetAsync(api.PanelClient(), shop.Owner, $"/api/painel/privacidade/acessos?pedido={order.Id}")).Content.ReadFromJsonAsync<AccessPageDto>())!;
        var entry = Assert.Single(page.Items);
        Assert.Equal((PersonalDataActions.OrderViewed, order.Id, (long?)order.Number), (entry.Action, entry.OrderId, entry.OrderNumber));
        Assert.EndsWith("@pecas.test", entry.UserEmail, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(entry.Ip));

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(api.PanelClient(), operador, "/api/painel/privacidade/acessos")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PrivacyAsync(shop, "titular/previa", new { email = "bia@exemplo.com.br" }, operador)).StatusCode);
        var otherPage = (await (await GetAsync(api.PanelClient(), other.Owner, "/api/painel/privacidade/acessos")).Content.ReadFromJsonAsync<AccessPageDto>())!;
        Assert.Empty(otherPage.Items);
    }

    [Fact]
    public async Task Registro_de_acesso_e_somente_inclusao_para_o_papel_da_aplicacao()
    {
        var shop = await ShopAsync();
        var order = await OrderAsync(shop, "caio@exemplo.com.br", TestBuyers.Cpf());
        await GetAsync(api.PanelClient(), shop.Owner, $"/api/painel/pedidos/{order.Id}");

        var update = await Assert.ThrowsAsync<PostgresException>(() =>
            InTenantAsync(shop.Tenant.Id, db => db.Database.ExecuteSqlAsync($"UPDATE personal_data_access_log SET action = 'x'")));
        var delete = await Assert.ThrowsAsync<PostgresException>(() =>
            InTenantAsync(shop.Tenant.Id, db => db.Database.ExecuteSqlAsync($"DELETE FROM personal_data_access_log")));

        Assert.Equal((PostgresErrorCodes.InsufficientPrivilege, PostgresErrorCodes.InsufficientPrivilege), (update.SqlState, delete.SqlState));
    }
}
