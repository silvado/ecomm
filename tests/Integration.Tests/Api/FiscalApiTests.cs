using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Ecommerce.Domain.Fiscal;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Ecommerce.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF19 parte A — NF-e com o provedor falso: configuração fiscal, dados da peça, emissão pela fila até autorizada (XML e
/// DANFE no storage privado), rejeição e reenvio, cancelamento no prazo, emissão externa por chave/XML e isolamento.
/// </summary>
public sealed class FiscalApiTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record PlacedDto(long Number);
    private sealed record DocumentDto(Guid Id, string Source, string Status, string? AccessKey, int? Number, string? Message, bool HasXml,
        bool HasDanfe, bool CanCancel);
    private sealed record AddressDto(string PostalCode, string Street, string Number, string? Complement, string District, string City, string State);
    private sealed record RecipientDto(string Name, string Document, string? Email, AddressDto? Address);
    private sealed record DraftDto(string Mode, RecipientDto? Recipient, List<string> Problems, List<DocumentDto> Documents);
    private sealed record SettingsDto(string Mode, string? State, string? DefaultNcm);
    private sealed record SettingsViewDto(SettingsDto Settings, string IssuerCnpj, bool ProviderConnected);
    private sealed record PartFiscalDto(string? Ncm, string? TaxCode, int? Origin);
    private sealed record PartDto(Guid Id, PartFiscalDto Fiscal);
    private sealed record ProblemDto(string Title);
    private sealed record LogDto(string Operation, bool Success);
    private sealed record LogPageDto(List<LogDto> Items);

    private sealed record Shop(Tenant Tenant, string Owner, HttpClient Store);

    private static int _ip;

    private static readonly object Address = new
    {
        postalCode = "88015-200", street = "Rua Felipe Schmidt", number = "100", complement = (string?)null, district = "Centro", city = "Florianópolis", state = "SC",
    };

    private async Task<Shop> ShopAsync(string mode = "provider")
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        await api.ConfigureShippingAsync(owner, tenant, "01310-100", pickup: true, "Balcão da loja");
        var shop = new Shop(tenant, owner, api.StoreClient(tenant, $"198.51.106.{Interlocked.Increment(ref _ip) % 250 + 1}"));
        var saved = await SendAsync(api.PanelClient(), owner, HttpMethod.Put, "/api/painel/loja/fiscal", Settings(mode));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        return shop;
    }

    private static object Settings(string mode, string? ncm = "87082999") => new
    {
        mode, environment = "homologation", regime = "simplesNacional", state = "SC", defaultNcm = ncm,
        cfopInState = "5102", cfopOutOfState = "6102", defaultTaxCode = "102", defaultOrigin = 0,
    };

    private async Task<(Guid Id, Guid Part)> PaidOrderAsync(Shop shop)
    {
        var part = await api.CreatePartAsync(shop.Owner, 100m);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var response = await shop.Store.PostAsJsonAsync("/api/loja/pedidos", new
        {
            token, expectedTotal = 100m, buyer = TestBuyers.Buyer(), items = new[] { new { partId = part, quantity = 1 } }, delivery = new { method = "pickup" },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var number = (await response.Content.ReadFromJsonAsync<PlacedDto>())!.Number;
        var id = await InTenantAsync(shop.Tenant.Id, db => db.CustomerOrders.Where(o => o.Number == number).Select(o => o.Id).SingleAsync());
        await InTenantAsync(shop.Tenant.Id, db => db.Database.ExecuteSqlAsync($"UPDATE customer_orders SET status = 'Paid', paid_at = now() WHERE id = {id}"));
        return (id, part);
    }

    private async Task<T> InTenantAsync<T>(Guid tenantId, Func<TenantDbContext, Task<T>> query)
    {
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        return await query(scope.ServiceProvider.GetRequiredService<TenantDbContext>());
    }

    private Task<HttpResponseMessage> EmitAsync(Shop shop, Guid order, bool withAddress = true) =>
        SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Post, $"/api/painel/pedidos/{order}/nfe", new
        {
            recipient = new { name = "Maria da Silva", document = TestBuyers.Cpf(), email = "maria@exemplo.com.br", address = withAddress ? Address : null },
        });

    private async Task<DraftDto> DraftAsync(Shop shop, Guid order) =>
        (await (await GetAsync(api.PanelClient(), shop.Owner, $"/api/painel/pedidos/{order}/nfe")).Content.ReadFromJsonAsync<DraftDto>())!;

    private async Task<DocumentDto> WaitForAsync(Shop shop, Guid order, Func<DocumentDto, bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var draft = await DraftAsync(shop, order);
            if (draft.Documents.FirstOrDefault() is { } latest && done(latest)) return latest;
            if (watch.Elapsed > TimeSpan.FromSeconds(30)) Assert.Fail($"NF-e ainda {draft.Documents.FirstOrDefault()?.Status ?? "inexistente"}.");
            await Task.Delay(200);
        }
    }

    private static string Key(string cnpj, int number = 77)
    {
        var body = string.Create(CultureInfo.InvariantCulture, $"422610{cnpj}55001{number:D9}112345678");
        return body + AccessKey.CheckDigit(body).ToString(CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Emite_pelo_provedor_ate_autorizada_com_xml_e_danfe()
    {
        var shop = await ShopAsync();
        var settings = (await (await GetAsync(api.PanelClient(), shop.Owner, "/api/painel/loja/fiscal")).Content.ReadFromJsonAsync<SettingsViewDto>())!;
        Assert.Equal(("provider", "SC", shop.Tenant.Cnpj.Value, true), (settings.Settings.Mode, settings.Settings.State, settings.IssuerCnpj, settings.ProviderConnected));

        var (order, _) = await PaidOrderAsync(shop);
        var draft = await DraftAsync(shop, order);
        Assert.Empty(draft.Problems);
        Assert.Null(draft.Recipient!.Address); // retirada: o painel pede o endereço

        var noAddress = await EmitAsync(shop, order, withAddress: false);
        Assert.Equal(HttpStatusCode.BadRequest, noAddress.StatusCode);
        Assert.Contains("endereço", (await noAddress.Content.ReadFromJsonAsync<ProblemDto>())!.Title, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await EmitAsync(shop, order)).StatusCode);
        var authorized = await WaitForAsync(shop, order, d => d is { Status: "authorized", HasXml: true });
        Assert.Equal(44, authorized.AccessKey!.Length);
        Assert.True(authorized.HasDanfe);
        Assert.True(authorized.CanCancel);

        var xml = await GetAsync(api.PanelClient(), shop.Owner, $"/api/painel/pedidos/{order}/nfe/{authorized.Id}/xml");
        Assert.Equal("application/xml", xml.Content.Headers.ContentType!.MediaType);
        Assert.Contains(authorized.AccessKey, await xml.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal($"NFe{authorized.AccessKey}.xml", xml.Content.Headers.ContentDisposition!.FileNameStar ?? xml.Content.Headers.ContentDisposition.FileName);
        var danfe = await GetAsync(api.PanelClient(), shop.Owner, $"/api/painel/pedidos/{order}/nfe/{authorized.Id}/danfe");
        Assert.StartsWith("%PDF", await danfe.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Segunda emissão para o mesmo pedido é recusada.
        Assert.Equal(HttpStatusCode.Conflict, (await EmitAsync(shop, order)).StatusCode);

        var log = (await (await GetAsync(api.PanelClient(), shop.Owner, $"/api/painel/integracoes/log?pedido={order}")).Content.ReadFromJsonAsync<LogPageDto>())!;
        Assert.Contains(log.Items, l => l.Operation == "emitir NF-e" && l.Success);
        Assert.Contains(log.Items, l => l.Operation == "consultar NF-e" && l.Success);
    }

    [Fact]
    public async Task Rejeitada_mostra_o_motivo_e_depois_de_corrigir_a_peca_e_reenviada()
    {
        var shop = await ShopAsync();
        var (order, part) = await PaidOrderAsync(shop);
        var bad = await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Put, $"/api/painel/pecas/{part}/fiscal", new { ncm = "00000000", taxCode = (string?)null, origin = (int?)null });
        Assert.Equal("00000000", (await bad.Content.ReadFromJsonAsync<PartDto>())!.Fiscal.Ncm);

        await EmitAsync(shop, order);
        var rejected = await WaitForAsync(shop, order, d => d.Status == "rejected");
        Assert.Contains("NCM", rejected.Message, StringComparison.Ordinal);

        await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Put, $"/api/painel/pecas/{part}/fiscal", new { ncm = "8708.29.99", taxCode = "102", origin = 0 });
        Assert.Equal(HttpStatusCode.OK, (await EmitAsync(shop, order)).StatusCode);
        await WaitForAsync(shop, order, d => d.Status == "authorized");
        var documents = (await DraftAsync(shop, order)).Documents;
        Assert.Equal(["authorized", "rejected"], documents.Select(d => d.Status));
    }

    [Fact]
    public async Task Cancela_no_prazo_com_justificativa_e_libera_nova_emissao()
    {
        var shop = await ShopAsync();
        var (order, _) = await PaidOrderAsync(shop);
        await EmitAsync(shop, order);
        var authorized = await WaitForAsync(shop, order, d => d.Status == "authorized");

        var shortReason = await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Post, $"/api/painel/pedidos/{order}/nfe/{authorized.Id}/cancelar", new { justification = "erro" });
        Assert.Equal(HttpStatusCode.BadRequest, shortReason.StatusCode);
        var canceled = await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Post, $"/api/painel/pedidos/{order}/nfe/{authorized.Id}/cancelar",
            new { justification = "Venda desfeita a pedido do cliente" });
        Assert.Equal("canceled", (await canceled.Content.ReadFromJsonAsync<DraftDto>())!.Documents[0].Status);

        // Fora do prazo de 24 h, não cancela.
        await EmitAsync(shop, order);
        var second = await WaitForAsync(shop, order, d => d.Status == "authorized");
        await InTenantAsync(shop.Tenant.Id, db => db.Database.ExecuteSqlAsync($"UPDATE fiscal_documents SET authorized_at = now() - interval '25 hours' WHERE id = {second.Id}"));
        var late = await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Post, $"/api/painel/pedidos/{order}/nfe/{second.Id}/cancelar",
            new { justification = "Venda desfeita a pedido do cliente" });
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Contains("devolução", (await late.Content.ReadFromJsonAsync<ProblemDto>())!.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pedido_nao_pago_ou_loja_sem_emissao_nao_emite()
    {
        var shop = await ShopAsync();
        var (order, _) = await PaidOrderAsync(shop);
        await InTenantAsync(shop.Tenant.Id, db => db.Database.ExecuteSqlAsync($"UPDATE customer_orders SET status = 'PendingPayment' WHERE id = {order}"));
        Assert.Equal(HttpStatusCode.Conflict, (await EmitAsync(shop, order)).StatusCode);

        await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Put, "/api/painel/loja/fiscal", Settings("disabled"));
        await InTenantAsync(shop.Tenant.Id, db => db.Database.ExecuteSqlAsync($"UPDATE customer_orders SET status = 'Paid' WHERE id = {order}"));
        var off = await EmitAsync(shop, order);
        Assert.Equal(HttpStatusCode.Conflict, off.StatusCode);
        Assert.Contains("desligada", (await off.Content.ReadFromJsonAsync<ProblemDto>())!.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Emissao_externa_confere_chave_cnpj_e_xml()
    {
        var shop = await ShopAsync("external");
        var (order, _) = await PaidOrderAsync(shop);
        var cnpj = shop.Tenant.Cnpj.Value;
        Task<HttpResponseMessage> Register(object body) => SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Post, $"/api/painel/pedidos/{order}/nfe/externa", body);

        Assert.Equal(HttpStatusCode.BadRequest, (await Register(new { accessKey = "123" })).StatusCode);
        var other = await Register(new { accessKey = Key("11222333000181") });
        Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);
        Assert.Contains("CNPJ da loja", (await other.Content.ReadFromJsonAsync<ProblemDto>())!.Title, StringComparison.Ordinal);

        var key = Key(cnpj);
        var xml = Encoding.UTF8.GetBytes($"""<?xml version="1.0"?><nfeProc><NFe><infNFe Id="NFe{key}"></infNFe></NFe><protNFe><infProt><chNFe>{key}</chNFe></infProt></protNFe></nfeProc>""");
        var mismatch = await Register(new { accessKey = Key(cnpj, 78), xmlBase64 = Convert.ToBase64String(xml) });
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);

        var doctype = Encoding.UTF8.GetBytes("""<?xml version="1.0"?><!DOCTYPE x [<!ENTITY e SYSTEM "file:///etc/passwd">]><x>&e;</x>""");
        Assert.Equal(HttpStatusCode.BadRequest, (await Register(new { xmlBase64 = Convert.ToBase64String(doctype) })).StatusCode);

        var ok = await Register(new { xmlBase64 = Convert.ToBase64String(xml) });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var document = (await ok.Content.ReadFromJsonAsync<DraftDto>())!.Documents.Single();
        Assert.Equal(("external", "authorized", key, 77, true), (document.Source, document.Status, document.AccessKey, document.Number!.Value, document.HasXml));

        // Pelo provedor, não (a loja está no modo externo).
        Assert.Equal(HttpStatusCode.Conflict, (await EmitAsync(shop, order)).StatusCode);
    }

    [Fact]
    public async Task Configuracao_e_dados_da_peca_validados_e_operador_nao_configura()
    {
        var shop = await ShopAsync();
        var incomplete = await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Put, "/api/painel/loja/fiscal", new { mode = "provider", environment = "homologation", regime = "simplesNacional" });
        Assert.Equal(HttpStatusCode.BadRequest, incomplete.StatusCode);

        var part = await api.CreatePartAsync(shop.Owner);
        var cst = await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Put, $"/api/painel/pecas/{part}/fiscal", new { ncm = "87082999", taxCode = "00", origin = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, cst.StatusCode); // CST no Simples Nacional

        var operatorToken = await api.SessionForAsync(shop.Tenant, TenantRole.Operator);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(api.PanelClient(), operatorToken, "/api/painel/loja/fiscal")).StatusCode);
        var (order, _) = await PaidOrderAsync(shop);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(api.PanelClient(), operatorToken, $"/api/painel/pedidos/{order}/nfe")).StatusCode);
    }

    [Fact]
    public async Task Notas_isoladas_por_loja()
    {
        var shop = await ShopAsync();
        var other = await ShopAsync();
        var (order, _) = await PaidOrderAsync(shop);
        await EmitAsync(shop, order);
        var document = await WaitForAsync(shop, order, d => d.Status == "authorized");

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(api.PanelClient(), other.Owner, $"/api/painel/pedidos/{order}/nfe")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(api.PanelClient(), other.Owner, $"/api/painel/pedidos/{order}/nfe/{document.Id}/xml")).StatusCode);
        Assert.False(await InTenantAsync(other.Tenant.Id, db => db.FiscalDocuments.IgnoreQueryFilters().AnyAsync()));
    }
}
