using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Ecommerce.Api.Panel;
using Ecommerce.Application.Channels;
using Ecommerce.Domain.Billing;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Billing;
using Ecommerce.Infrastructure.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF38 CA2/CA3 — régua de inadimplência: D+10 painel só leitura (a loja continua vendendo), D+20 vitrine fora do ar e
/// anúncios pausados nos canais, volta automática ao pagar; suspensão da equipe prevalece e não volta com o pagamento.
/// </summary>
public sealed class DunningTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record StandingDto(string Status, string Stage, DateOnly? OverdueSince, bool ReadOnly);
    private sealed record ProblemDto(string Title, string? Type);
    private sealed record InvoiceDto(Guid Id, string Status);
    private sealed record SubscriptionDto(List<InvoiceDto> Invoices);
    private sealed record PayDto(string RedirectUrl);
    private sealed record ListingDto(Guid Id, string? ExternalId, string Status);
    private sealed record Shop(Tenant Tenant, string Owner, Guid Part);

    private static DateOnly Today => BillingCalendar.Today(TimeProvider.System);

    private Task<Guid> OverdueAsync(Guid tenantId, int daysLate) => api.WithPlatformAsync(async db =>
    {
        var invoice = SubscriptionInvoice.Setup(tenantId, 500m, Today.AddDays(-daysLate), DateTimeOffset.UtcNow);
        db.SubscriptionInvoices.Add(invoice);
        await db.SaveChangesAsync();
        return invoice.Id;
    });

    private Task<SubscriptionBillingRun> RunJobAsync() =>
        ActivatorUtilities.CreateInstance<SubscriptionBillingJob>(api.Services).RunAsync(CancellationToken.None);

    private async Task<(TenantStatus Status, bool Hold)> StatusAsync(Guid tenantId)
    {
        var row = await api.WithPlatformAsync(db => db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => new { t.Status, t.BillingHold }).SingleAsync());
        return (row.Status, row.BillingHold);
    }

    private async Task<Shop> ShopWithListingAsync()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var client = api.PanelClient();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, owner, HttpMethod.Post, "/api/painel/canal-simulado/conectar",
            new { conta = "vend-" + Guid.NewGuid().ToString("N")[..10] })).StatusCode);
        var part = await api.CreatePartAsync(owner, 100m, 2);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, owner, HttpMethod.Post, "/api/painel/canal-simulado/publicar", new { pecaId = part })).StatusCode);
        return new Shop(tenant, owner, part);
    }

    private async Task<ListingDto> ListingAsync(Shop shop) =>
        Assert.Single((await (await GetAsync(api.PanelClient(), shop.Owner, $"/api/painel/pecas/{shop.Part}/anuncios"))
            .Content.ReadFromJsonAsync<List<ListingDto>>())!);

    private async Task WaitListingAsync(Shop shop, string status, RemoteListingStatus remote)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var listing = await ListingAsync(shop);
            if (listing.Status == status && FakeChannelConnector.Remote(listing.ExternalId!)?.Status == remote) return;
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) Assert.Fail($"Anúncio ainda {listing.Status}, esperado {status}.");
            await Task.Delay(100);
        }
    }

    private static Task<HttpResponseMessage> SaveStoreAsync(HttpClient client, string token) =>
        SendAsync(client, token, HttpMethod.Put, "/api/painel/loja", new
        {
            tradeName = "Loja da régua", primaryColor = "#b42318", backgroundColor = "#ffffff", textColor = "#101828",
            about = "", returnPolicy = "", footer = "",
            shipping = new { originPostalCode = "01310-100", pickupEnabled = false, pickupAddress = (string?)null },
        });

    [Fact]
    public async Task Aviso_e_depois_painel_so_leitura_com_a_loja_vendendo_e_pagar_libera_na_hora()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var operatorToken = await api.SessionForAsync(tenant, TenantRole.Operator);
        var client = api.PanelClient();
        var invoiceId = await OverdueAsync(tenant.Id, 4);

        await RunJobAsync();
        var standing = (await (await GetAsync(client, operatorToken, "/api/painel/assinatura/situacao")).Content.ReadFromJsonAsync<StandingDto>())!;
        Assert.Equal(("active", "warned", false), (standing.Status, standing.Stage, standing.ReadOnly));
        Assert.Equal(HttpStatusCode.OK, (await SaveStoreAsync(client, owner)).StatusCode);

        // Mesma fatura, agora com 11 dias de atraso.
        await api.WithPlatformAsync(db => db.SubscriptionInvoices.Where(i => i.Id == invoiceId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.DueDate, Today.AddDays(-11))));
        var run = await RunJobAsync();
        Assert.Contains(run.Changes, c => c.TenantId == tenant.Id && c.To == TenantStatus.ReadOnly);
        Assert.Equal((TenantStatus.ReadOnly, true), await StatusAsync(tenant.Id));

        standing = (await (await GetAsync(client, owner, "/api/painel/assinatura/situacao")).Content.ReadFromJsonAsync<StandingDto>())!;
        Assert.Equal(("readOnly", "readOnly", true, Today.AddDays(-11)), (standing.Status, standing.Stage, standing.ReadOnly, standing.OverdueSince!.Value));
        var refused = await SaveStoreAsync(client, owner);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(PanelReadOnlyFilter.ProblemType, (await refused.Content.ReadFromJsonAsync<ProblemDto>())!.Type);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, owner, "/api/painel/loja")).StatusCode);
        // Q13 (decisão do usuário): a vitrine continua vendendo; LGPD continua valendo.
        Assert.Equal(HttpStatusCode.OK, (await api.StoreClient(tenant).GetAsync("/api/loja/identidade")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(client, owner, HttpMethod.Post, "/api/painel/privacidade/exportacoes", null)).StatusCode);

        // Paga pelo gateway (simulado): a loja volta sem esperar o job.
        var pay = await SendAsync(client, owner, HttpMethod.Post, $"/api/painel/assinatura/faturas/{invoiceId}/pagar", null);
        Assert.Equal(HttpStatusCode.OK, pay.StatusCode);
        var chargeId = await api.WithPlatformAsync(db => db.SubscriptionInvoices.Where(i => i.Id == invoiceId).Select(i => i.ChargeId!).SingleAsync());
        var paid = FakeSubscriptionBilling.PaidChargeId(chargeId);
        var requestId = Guid.NewGuid().ToString();
        var notify = new HttpRequestMessage(HttpMethod.Post,
            $"/api/webhooks/assinatura/{FakeSubscriptionBilling.GatewayName}?evento=cobranca&id={Uri.EscapeDataString(paid)}");
        notify.Headers.Add("x-request-id", requestId);
        notify.Headers.Add(FakeSubscriptionBilling.SignatureHeader, FakeSubscriptionBilling.Sign(paid, requestId));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(notify)).StatusCode);

        var watch = Stopwatch.StartNew();
        while (await StatusAsync(tenant.Id) != (TenantStatus.Active, false))
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) Assert.Fail("A loja não voltou depois do pagamento.");
            await Task.Delay(100);
        }
        Assert.Equal(HttpStatusCode.OK, (await SaveStoreAsync(client, owner)).StatusCode);
    }

    [Fact]
    public async Task Suspensa_pela_regua_sai_do_ar_pausa_os_anuncios_e_volta_quando_a_equipe_registra_o_pagamento()
    {
        var shop = await ShopWithListingAsync();
        await WaitListingAsync(shop, "active", RemoteListingStatus.Active);
        var invoiceId = await OverdueAsync(shop.Tenant.Id, 21);

        await RunJobAsync();

        Assert.Equal((TenantStatus.Suspended, true), await StatusAsync(shop.Tenant.Id));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await api.StoreClient(shop.Tenant).GetAsync("/api/loja/identidade")).StatusCode);
        await WaitListingAsync(shop, "pausedBySystem", RemoteListingStatus.Paused);
        // Mudar estoque com a loja suspensa não reativa o anúncio (e o painel nem deixa).
        Assert.Equal(HttpStatusCode.Forbidden, (await SaveStoreAsync(api.PanelClient(), shop.Owner)).StatusCode);

        var client = api.PanelClient();
        var admin = await PlatformAdminTests.EnrolledAdminAsync(api, client);
        var reactivate = await SendAsync(client, admin.Token, HttpMethod.Post, $"/api/plataforma/lojas/{shop.Tenant.Id}/reativar", null);
        Assert.Equal(HttpStatusCode.BadRequest, reactivate.StatusCode);
        Assert.Contains("fatura em atraso", (await reactivate.Content.ReadFromJsonAsync<ProblemDto>())!.Title, StringComparison.Ordinal);

        var manual = await SendAsync(client, admin.Token, HttpMethod.Post, $"/api/plataforma/lojas/{shop.Tenant.Id}/cobranca/faturas/{invoiceId}/pagamento-manual",
            new { observacao = "Pix recebido" });
        Assert.Equal(HttpStatusCode.OK, manual.StatusCode);
        Assert.Equal((TenantStatus.Active, false), await StatusAsync(shop.Tenant.Id));
        Assert.Equal(HttpStatusCode.OK, (await api.StoreClient(shop.Tenant).GetAsync("/api/loja/identidade")).StatusCode);
        await WaitListingAsync(shop, "active", RemoteListingStatus.Active);
    }

    [Fact]
    public async Task Suspensao_da_equipe_pausa_os_anuncios_e_nao_volta_com_o_pagamento()
    {
        var shop = await ShopWithListingAsync();
        await WaitListingAsync(shop, "active", RemoteListingStatus.Active);
        var client = api.PanelClient();
        var admin = await PlatformAdminTests.EnrolledAdminAsync(api, client);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, admin.Token, HttpMethod.Post, $"/api/plataforma/lojas/{shop.Tenant.Id}/suspender", null)).StatusCode);
        await WaitListingAsync(shop, "pausedBySystem", RemoteListingStatus.Paused);

        var invoiceId = await OverdueAsync(shop.Tenant.Id, 12);
        await RunJobAsync();
        Assert.Equal((TenantStatus.Suspended, false), await StatusAsync(shop.Tenant.Id));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, admin.Token, HttpMethod.Post,
            $"/api/plataforma/lojas/{shop.Tenant.Id}/cobranca/faturas/{invoiceId}/pagamento-manual", new { observacao = "Pix" })).StatusCode);
        Assert.Equal((TenantStatus.Suspended, false), await StatusAsync(shop.Tenant.Id));

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, admin.Token, HttpMethod.Post, $"/api/plataforma/lojas/{shop.Tenant.Id}/reativar", null)).StatusCode);
        await WaitListingAsync(shop, "active", RemoteListingStatus.Active);
    }

    [Fact]
    public async Task Regua_de_uma_loja_nao_afeta_outra()
    {
        var late = await api.CreateTenantAsync();
        var onTime = await api.CreateTenantAsync();
        var onTimeOwner = await api.SessionForAsync(onTime);
        await OverdueAsync(late.Id, 15);

        await RunJobAsync();

        Assert.Equal(TenantStatus.ReadOnly, (await StatusAsync(late.Id)).Status);
        Assert.Equal(TenantStatus.Active, (await StatusAsync(onTime.Id)).Status);
        var standing = (await (await GetAsync(api.PanelClient(), onTimeOwner, "/api/painel/assinatura/situacao")).Content.ReadFromJsonAsync<StandingDto>())!;
        Assert.Equal(("none", false), (standing.Stage, standing.ReadOnly));
        Assert.Equal(HttpStatusCode.OK, (await SaveStoreAsync(api.PanelClient(), onTimeOwner)).StatusCode);
    }
}
