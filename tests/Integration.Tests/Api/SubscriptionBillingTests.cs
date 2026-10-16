using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Ecommerce.Domain.Billing;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF38 — cobrança da assinatura: mensalidade gerada pelo job a partir da publicação (sem duplicar, sem retroativo), Dono
/// paga pelo gateway da plataforma (simulado), aviso reentregue não repete, e a equipe lança implantação, registra
/// pagamento manual e cancela — tudo auditado.
/// </summary>
public sealed class SubscriptionBillingTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record InvoiceDto(Guid Id, string Kind, string Description, decimal Amount, DateOnly DueDate, string Status, bool Overdue, string? PaidVia,
        string? Note);
    private sealed record SubscriptionDto(string PlanCode, decimal MonthlyPrice, DateOnly? StartedOn, int? DueDay, bool OnlinePayment, List<InvoiceDto> Invoices);
    private sealed record PayDto(string RedirectUrl);
    private sealed record BillingDto(Guid TenantId, decimal MonthlyPrice, DateOnly? StartedOn, List<InvoiceDto> Invoices);
    private sealed record PlanDto(string Code, decimal MonthlyPrice);

    private const decimal Price = 199.90m;

    private Task SetPriceAsync(string planCode = Plan.Essential, decimal price = Price) => api.WithPlatformAsync(async db =>
    {
        var plan = await db.Plans.SingleAsync(p => p.Code == planCode);
        plan.SetMonthlyPrice(price);
        await db.SaveChangesAsync();
    });

    private Task<SubscriptionBillingRun> RunJobAsync() =>
        ActivatorUtilities.CreateInstance<SubscriptionBillingJob>(api.Services).RunAsync(CancellationToken.None);

    private static DateOnly Today => BillingCalendar.Today(TimeProvider.System);

    private async Task<SubscriptionDto> SubscriptionAsync(string token)
    {
        var response = await GetAsync(api.PanelClient(), token, "/api/painel/assinatura");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SubscriptionDto>())!;
    }

    [Fact]
    public async Task Job_inicia_a_assinatura_da_loja_publicada_e_gera_a_mensalidade_uma_vez()
    {
        await SetPriceAsync();
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);

        await RunJobAsync();
        await RunJobAsync();

        var view = await SubscriptionAsync(owner);
        Assert.Equal((Today, Math.Min(Today.Day, Subscription.MaxDueDay)), (view.StartedOn!.Value, view.DueDay!.Value));
        var invoice = Assert.Single(view.Invoices);
        Assert.Equal(("monthly", Price, Today, "open", false), (invoice.Kind, invoice.Amount, invoice.DueDate, invoice.Status, invoice.Overdue));
        Assert.True(view.OnlinePayment);
    }

    [Fact]
    public async Task Nao_cobra_retroativo_nem_loja_em_implantacao()
    {
        await SetPriceAsync();
        var old = await api.CreateTenantAsync();
        var onboarding = await api.CreateTenantAsync(published: false);
        // Assinatura começada há ~2 meses e meio, sem nenhuma fatura (preço zerado na época): só entra o que vence de hoje em diante.
        await api.WithPlatformAsync(async db =>
        {
            db.Subscriptions.Add(Subscription.Start(old.Id, Today.AddDays(-75), DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        });

        await RunJobAsync();

        var invoices = await api.WithPlatformAsync(db => db.SubscriptionInvoices.AsNoTracking().Where(i => i.TenantId == old.Id).ToListAsync());
        Assert.All(invoices, i => Assert.True(i.DueDate >= Today));
        Assert.True(invoices.Count <= 1);
        Assert.False(await api.WithPlatformAsync(db => db.Subscriptions.AnyAsync(s => s.TenantId == onboarding.Id)));
        Assert.False(await api.WithPlatformAsync(db => db.SubscriptionInvoices.AnyAsync(i => i.TenantId == onboarding.Id)));
    }

    [Fact]
    public async Task Dono_paga_pelo_gateway_simulado_e_aviso_repetido_nao_duplica()
    {
        await SetPriceAsync();
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        await RunJobAsync();
        var invoice = Assert.Single((await SubscriptionAsync(owner)).Invoices);
        var client = api.PanelClient();

        var pay = await SendAsync(client, owner, HttpMethod.Post, $"/api/painel/assinatura/faturas/{invoice.Id}/pagar", null);
        Assert.Equal(HttpStatusCode.OK, pay.StatusCode);
        var url = (await pay.Content.ReadFromJsonAsync<PayDto>())!.RedirectUrl;
        Assert.StartsWith(FakeSubscriptionBilling.SimulationPath, url, StringComparison.Ordinal);
        // Pagar de novo reaproveita a mesma cobrança (nunca duas para pagar).
        var again = await SendAsync(client, owner, HttpMethod.Post, $"/api/painel/assinatura/faturas/{invoice.Id}/pagar", null);
        Assert.Equal(url, (await again.Content.ReadFromJsonAsync<PayDto>())!.RedirectUrl);

        var page = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("R$ 199,90", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // O aviso do gateway, como o simulado manda; assinatura errada é recusada sem gravar nada.
        var chargeId = await api.WithPlatformAsync(db => db.SubscriptionInvoices.Where(i => i.Id == invoice.Id).Select(i => i.ChargeId!).SingleAsync());
        var paid = FakeSubscriptionBilling.PaidChargeId(chargeId);
        var requestId = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.Unauthorized, (await NotifyAsync(client, paid, requestId, "assinatura-errada")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await NotifyAsync(client, paid, requestId, FakeSubscriptionBilling.Sign(paid, requestId))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await NotifyAsync(client, paid, requestId, FakeSubscriptionBilling.Sign(paid, requestId))).StatusCode);

        var view = await WaitForAsync(() => SubscriptionAsync(owner), v => v.Invoices.Single().Status == "paid", "a fatura paga");
        Assert.Equal("gateway", view.Invoices.Single().PaidVia);
        Assert.Equal(1, await api.WithPlatformAsync(db => db.SubscriptionEvents.CountAsync(e => e.ChargeId == paid)));
        Assert.Equal(HttpStatusCode.Conflict,
            (await SendAsync(client, owner, HttpMethod.Post, $"/api/painel/assinatura/faturas/{invoice.Id}/pagar", null)).StatusCode);
    }

    [Fact]
    public async Task Pagina_simulada_quita_e_volta_para_a_tela_assinatura()
    {
        await SetPriceAsync();
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        await RunJobAsync();
        var invoice = Assert.Single((await SubscriptionAsync(owner)).Invoices);
        var client = api.PanelClient();
        var url = (await (await SendAsync(client, owner, HttpMethod.Post, $"/api/painel/assinatura/faturas/{invoice.Id}/pagar", null))
            .Content.ReadFromJsonAsync<PayDto>())!.RedirectUrl;

        // Volta para fora da tela Assinatura: recusado (redirecionamento aberto).
        var chargeId = url.Split('?')[0][(FakeSubscriptionBilling.SimulationPath.Length + 1)..];
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsync($"{FakeSubscriptionBilling.SimulationPath}/{chargeId}/pagar?volta={Uri.EscapeDataString("https://mal.test/x")}", null)).StatusCode);

        var noRedirect = api.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var back = await noRedirect.PostAsync(url.Replace("?", "/pagar?", StringComparison.Ordinal), null);
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        Assert.EndsWith("/assinatura", back.Headers.Location!.ToString(), StringComparison.Ordinal);
        await WaitForAsync(() => SubscriptionAsync(owner), v => v.Invoices.Single().Status == "paid", "a fatura paga");
    }

    [Fact]
    public async Task Operador_nao_ve_e_outra_loja_nao_paga_a_fatura()
    {
        await SetPriceAsync();
        var tenant = await api.CreateTenantAsync();
        var other = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        await RunJobAsync();
        var invoice = Assert.Single((await SubscriptionAsync(owner)).Invoices);

        var operatorToken = await api.SessionForAsync(tenant, TenantRole.Operator);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(api.PanelClient(), operatorToken, "/api/painel/assinatura")).StatusCode);

        var otherOwner = await api.SessionForAsync(other);
        Assert.DoesNotContain((await SubscriptionAsync(otherOwner)).Invoices, i => i.Id == invoice.Id);
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(api.PanelClient(), otherOwner, HttpMethod.Post, $"/api/painel/assinatura/faturas/{invoice.Id}/pagar", null)).StatusCode);
    }

    [Fact]
    public async Task Publicar_a_loja_comeca_a_assinatura_com_a_primeira_mensalidade_vencendo_hoje()
    {
        await SetPriceAsync();
        var tenant = await api.CreateTenantAsync(published: false);
        var owner = await api.SessionForAsync(tenant);
        var client = api.PanelClient();
        var profile = await (await GetAsync(client, owner, "/api/painel/loja")).Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, owner, HttpMethod.Put, "/api/painel/loja", new
        {
            tradeName = (string)profile!["tradeName"]!, primaryColor = "#b42318", backgroundColor = "#ffffff", textColor = "#101828",
            about = "", returnPolicy = "", footer = "",
            shipping = new { originPostalCode = "01310-100", pickupEnabled = false, pickupAddress = (string?)null },
        })).StatusCode);
        Assert.Empty((await SubscriptionAsync(owner)).Invoices);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, owner, HttpMethod.Post, "/api/painel/primeiros-passos/publicar", null)).StatusCode);

        var view = await SubscriptionAsync(owner);
        Assert.Equal(Today, view.StartedOn);
        Assert.Equal(Today, Assert.Single(view.Invoices).DueDate);
    }

    [Fact]
    public async Task Equipe_define_preco_lanca_implantacao_registra_pagamento_e_cancela_com_auditoria()
    {
        var client = api.PanelClient();
        var admin = await PlatformAdminTests.EnrolledAdminAsync(api, client);
        var tenant = await api.CreateTenantAsync(planCode: Plan.Complete);

        var price = await SendAsync(client, admin.Token, HttpMethod.Put, $"/api/plataforma/planos/{Plan.Complete}/preco", new { valor = 459.90m });
        Assert.Equal(HttpStatusCode.OK, price.StatusCode);
        Assert.Equal(459.90m, (await price.Content.ReadFromJsonAsync<List<PlanDto>>())!.Single(p => p.Code == Plan.Complete).MonthlyPrice);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendAsync(client, admin.Token, HttpMethod.Put, $"/api/plataforma/planos/{Plan.Complete}/preco", new { valor = 1.999m })).StatusCode);

        var basePath = $"/api/plataforma/lojas/{tenant.Id}/cobranca";
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendAsync(client, admin.Token, HttpMethod.Post, $"{basePath}/implantacao", new { valor = 1500m, vencimento = Today.AddDays(-1) })).StatusCode);
        var setup = await SendAsync(client, admin.Token, HttpMethod.Post, $"{basePath}/implantacao", new { valor = 1500m, vencimento = Today.AddDays(7) });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var fee = Assert.Single((await setup.Content.ReadFromJsonAsync<BillingDto>())!.Invoices);
        Assert.Equal(("setup", 1500m), (fee.Kind, fee.Amount));
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendAsync(client, admin.Token, HttpMethod.Post, $"{basePath}/implantacao", new { valor = 1500m, vencimento = Today.AddDays(7) })).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendAsync(client, admin.Token, HttpMethod.Post, $"{basePath}/faturas/{fee.Id}/pagamento-manual", new { observacao = "" })).StatusCode);
        var manual = await SendAsync(client, admin.Token, HttpMethod.Post, $"{basePath}/faturas/{fee.Id}/pagamento-manual", new { observacao = "Pix em 16/10" });
        Assert.Equal(HttpStatusCode.OK, manual.StatusCode);
        var paidFee = (await manual.Content.ReadFromJsonAsync<BillingDto>())!.Invoices.Single();
        Assert.Equal(("paid", "manual", "Pix em 16/10"), (paidFee.Status, paidFee.PaidVia, paidFee.Note));

        await RunJobAsync();
        var monthly = (await (await GetAsync(client, admin.Token, basePath)).Content.ReadFromJsonAsync<BillingDto>())!.Invoices.Single(i => i.Kind == "monthly");
        Assert.Equal(459.90m, monthly.Amount);
        var canceled = await SendAsync(client, admin.Token, HttpMethod.Post, $"{basePath}/faturas/{monthly.Id}/cancelar", new { observacao = "Cortesia do 1º mês" });
        Assert.Equal("canceled", (await canceled.Content.ReadFromJsonAsync<BillingDto>())!.Invoices.Single(i => i.Id == monthly.Id).Status);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendAsync(client, admin.Token, HttpMethod.Post, $"{basePath}/faturas/{monthly.Id}/cancelar", new { observacao = "de novo" })).StatusCode);
        // Fatura de outra loja pela URL desta: não encontrada.
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(client, admin.Token, HttpMethod.Post, $"/api/plataforma/lojas/{Guid.NewGuid()}/cobranca/faturas/{fee.Id}/cancelar", new { observacao = "x" })).StatusCode);

        var actions = await api.WithPlatformAsync(db => db.PlatformAuditLog.AsNoTracking()
            .Where(e => e.AdminEmail == admin.Email).Select(e => e.Action).ToListAsync());
        Assert.All(
            new[] { PlatformAuditActions.PlanPriceChanged, PlatformAuditActions.SetupFeeAdded, PlatformAuditActions.InvoicePaidManually, PlatformAuditActions.InvoiceCanceled },
            action => Assert.Single(actions, action));
    }

    private static Task<HttpResponseMessage> NotifyAsync(HttpClient client, string chargeId, string requestId, string signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/webhooks/assinatura/{FakeSubscriptionBilling.GatewayName}?evento=cobranca&id={Uri.EscapeDataString(chargeId)}");
        request.Headers.Add("x-request-id", requestId);
        request.Headers.Add(FakeSubscriptionBilling.SignatureHeader, signature);
        return client.SendAsync(request);
    }

    private static async Task<T> WaitForAsync<T>(Func<Task<T>> read, Func<T, bool> done, string what, int seconds = 30)
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
}
