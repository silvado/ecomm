using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Ecommerce.Application.Reports;
using Ecommerce.Domain.Channels;
using Ecommerce.Domain.Identity;
using Ecommerce.Infrastructure.Email;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Reports;
using Ecommerce.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF34 — resumo diário: números de consultas (vendas de ontem por canal, peças paradas há mais de 90 dias, perguntas sem
/// resposta), no horário da loja, uma vez por dia, para os Donos; desligado não sai; cada loja vê só os seus números.
/// </summary>
public sealed class DailySummaryTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    private sealed record SettingsDto(bool Enabled, int Hour);
    private sealed record SalesDto(string Origin, int Orders, decimal Total);
    private sealed record FactsDto(List<SalesDto> Sales, int StaleParts, int PendingQuestions);

    private InMemoryEmailOutbox Outbox => api.Services.GetRequiredService<InMemoryEmailOutbox>();

    private static int CurrentHour => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, SaoPaulo).Hour;

    private async Task<T> InTenantAsync<T>(Guid tenantId, Func<TenantDbContext, Task<T>> query)
    {
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        return await query(scope.ServiceProvider.GetRequiredService<TenantDbContext>());
    }

    private async Task<DailySummaryOutcome> PrepareAsync(Guid tenantId, params string[] owners)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeForTenantAsync<DailySummaryOutcome>(tenantId.ToString(),
            new PrepareDailySummary(owners.Select(o => new DailySummaryRecipient(o)).ToList()));
    }

    private Task<HttpResponseMessage> ConfigureAsync(string session, bool enabled, int hour) =>
        SendAsync(api.PanelClient(), session, HttpMethod.Put, "/api/painel/resumo-diario", new { ativo = enabled, hora = hour });

    private static string Token() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<SentEmail> EmailAsync(string to)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (Outbox.Sent.FirstOrDefault(e => e.Message.To == to && e.Purpose == "resumo diário") is { } email) return email;
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) Assert.Fail($"Resumo não chegou para {to}.");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Resumo_sai_no_horario_com_os_numeros_das_consultas_e_uma_vez_por_dia()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        Assert.Equal(HttpStatusCode.OK, (await ConfigureAsync(owner, true, CurrentHour)).StatusCode);

        // Venda de balcão paga ontem (14h em Brasília) e uma de hoje, que não entra.
        var sold = await api.CreatePartAsync(owner, price: 120m, quantity: 5);
        foreach (var _ in new[] { 1, 2 })
            Assert.Equal(HttpStatusCode.Created, (await SendAsync(api.PanelClient(), owner, HttpMethod.Post, "/api/painel/balcao", new
            {
                token = Token(), items = new[] { new { partId = sold, quantity = 1, unitPrice = 120m } }, paymentMethod = "pix", customer = (object?)null,
            })).StatusCode);
        var yesterday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, SaoPaulo).DateTime).AddDays(-1);
        var paidYesterday = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(yesterday.ToDateTime(new TimeOnly(14, 0)), SaoPaulo), TimeSpan.Zero);
        await InTenantAsync(tenant.Id, db => db.Database.ExecuteSqlAsync(
            $"UPDATE customer_orders SET paid_at = {paidYesterday} WHERE id = (SELECT id FROM customer_orders ORDER BY number LIMIT 1)"));

        // Peça parada: cadastrada há 120 dias, com saldo e sem venda.
        var stale = await api.CreatePartAsync(owner, price: 80m, quantity: 1);
        await InTenantAsync(tenant.Id, db => db.Database.ExecuteSqlAsync($"UPDATE parts SET created_at = now() - interval '120 days' WHERE id = {stale}"));

        // Pergunta de comprador sem resposta.
        await InTenantAsync(tenant.Id, async db =>
        {
            var connection = new ChannelConnection(tenant.Id, SalesChannel.MercadoLivre, Random.Shared.Next(1_000_000, 9_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture), DateTimeOffset.UtcNow);
            db.ChannelConnections.Add(connection);
            db.BuyerQuestions.Add(new BuyerQuestion(tenant.Id, connection.Id, "123456", "MLB123", null, "Ainda tem?", DateTimeOffset.UtcNow.AddHours(-3), DateTimeOffset.UtcNow));
            return await db.SaveChangesAsync();
        });

        var preview = (await (await GetAsync(api.PanelClient(), owner, "/api/painel/resumo-diario/previa")).Content.ReadFromJsonAsync<FactsDto>())!;
        Assert.Equal(("counter", 1, 120m), (Assert.Single(preview.Sales).Origin, preview.Sales[0].Orders, preview.Sales[0].Total));
        Assert.Equal((1, 1), (preview.StaleParts, preview.PendingQuestions));

        var to = $"dono-{tenant.Slug}@loja.test";
        Assert.Equal(DailySummaryOutcome.Sent, await PrepareAsync(tenant.Id, to, to.ToUpperInvariant()));
        var email = await EmailAsync(to);
        Assert.Equal(tenant.Id, email.TenantId);
        Assert.StartsWith($"Resumo de {yesterday:dd/MM/yyyy}", email.Message.Subject, StringComparison.Ordinal);
        Assert.Contains("Balcão: 1 pedido, R$ 120,00", email.Message.Text, StringComparison.Ordinal);
        Assert.Contains("1 peça com estoque está sem venda há mais de 90 dias.", email.Message.Text, StringComparison.Ordinal);
        Assert.Contains("1 pergunta de comprador espera resposta", email.Message.Text, StringComparison.Ordinal);
        Assert.Contains("Bom dia! Aqui está o resumo de ontem", email.Message.Text, StringComparison.Ordinal); // abertura da IA (simulada) aceita

        // Na rodada seguinte (15 min depois) não sai de novo; o mesmo e-mail em maiúsculas não recebeu em dobro.
        Assert.Equal(DailySummaryOutcome.Skipped, await PrepareAsync(tenant.Id, to));
        await Task.Delay(500);
        Assert.Single(Outbox.Sent, e => e.TenantId == tenant.Id && e.Purpose == "resumo diário");
        Assert.True((await InTenantAsync(tenant.Id, db => db.DailySummaries.SingleAsync())).AiText);
    }

    [Fact]
    public async Task Desligado_ou_antes_do_horario_nao_sai()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var defaults = (await (await GetAsync(api.PanelClient(), owner, "/api/painel/resumo-diario")).Content.ReadFromJsonAsync<SettingsDto>())!;
        Assert.Equal(new SettingsDto(true, 7), defaults); // CA1: padrão 07:00

        Assert.Equal(HttpStatusCode.BadRequest, (await ConfigureAsync(owner, true, 24)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ConfigureAsync(owner, false, CurrentHour)).StatusCode);
        Assert.Equal(DailySummaryOutcome.Skipped, await PrepareAsync(tenant.Id, "dono@loja.test"));

        if (CurrentHour < 23)
        {
            Assert.Equal(HttpStatusCode.OK, (await ConfigureAsync(owner, true, CurrentHour + 1)).StatusCode);
            Assert.Equal(DailySummaryOutcome.Skipped, await PrepareAsync(tenant.Id, "dono@loja.test"));
        }
        Assert.False(await InTenantAsync(tenant.Id, db => db.DailySummaries.AnyAsync()));
    }

    [Fact]
    public async Task Agendador_manda_para_os_Donos_da_loja_publicada()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        Assert.Equal(HttpStatusCode.OK, (await ConfigureAsync(owner, true, 0)).StatusCode);
        var operatorUser = await api.CreateUserAsync(tenant.Id, TenantRole.Operator, "Senha-forte-123");
        var ownerEmail = await api.WithPlatformAsync(db => (from m in db.TenantMemberships
                                                             join u in db.UserAccounts on m.UserId equals u.Id
                                                             where m.TenantId == tenant.Id && m.Role == TenantRole.Owner
                                                             select u.Email).FirstAsync());
        var unpublished = await api.CreateTenantAsync(published: false);

        var scheduler = ActivatorUtilities.CreateInstance<DailySummaryScheduler>(api.Services);
        Assert.True(await scheduler.ScheduleAllAsync(CancellationToken.None) >= 1);

        await EmailAsync(ownerEmail);
        await Task.Delay(500);
        Assert.DoesNotContain(Outbox.Sent, e => e.Message.To == operatorUser.Email && e.Purpose == "resumo diário");
        Assert.DoesNotContain(Outbox.Sent, e => e.TenantId == unpublished.Id && e.Purpose == "resumo diário");
    }

    [Fact]
    public async Task Cada_loja_ve_so_os_proprios_numeros()
    {
        var tenant = await api.CreateTenantAsync();
        var other = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var otherOwner = await api.SessionForAsync(other);
        var stale = await api.CreatePartAsync(owner, price: 80m, quantity: 1);
        await InTenantAsync(tenant.Id, db => db.Database.ExecuteSqlAsync($"UPDATE parts SET created_at = now() - interval '120 days' WHERE id = {stale}"));
        Assert.Equal(HttpStatusCode.OK, (await ConfigureAsync(owner, true, 0)).StatusCode);
        Assert.Equal(DailySummaryOutcome.Sent, await PrepareAsync(tenant.Id, "a@loja.test"));

        var preview = (await (await GetAsync(api.PanelClient(), otherOwner, "/api/painel/resumo-diario/previa")).Content.ReadFromJsonAsync<FactsDto>())!;
        Assert.Equal(0, preview.StaleParts);
        Assert.False(await InTenantAsync(other.Id, db => db.DailySummaries.IgnoreQueryFilters().AnyAsync()));
    }
}
