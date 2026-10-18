using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Ecommerce.Application.Reports;
using Ecommerce.Domain.Channels;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Integrations;
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
/// RF33 — guardião da sincronização: problemas das consultas (canal sem autorização, falha em segundo plano, chamada que
/// segue falhando), diagnóstico com ação sugerida no painel e por e-mail aos Donos, sem repetir o mesmo problema em 24
/// horas; desligado não avisa; cada loja vê só os seus problemas.
/// </summary>
public sealed class SyncGuardianTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private const string Purpose = "alerta da sincronização";

    private sealed record DiagnosisDto(string Key, string Kind, string Title, int Occurrences, string Explanation, string Action, string ActionText);
    private sealed record SettingsDto(bool Enabled);

    private InMemoryEmailOutbox Outbox => api.Services.GetRequiredService<InMemoryEmailOutbox>();

    private async Task<T> InTenantAsync<T>(Guid tenantId, Func<TenantDbContext, Task<T>> query)
    {
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        return await query(scope.ServiceProvider.GetRequiredService<TenantDbContext>());
    }

    private async Task<SyncAlertOutcome> CheckAsync(Guid tenantId, params string[] owners)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeForTenantAsync<SyncAlertOutcome>(tenantId.ToString(),
            new CheckSyncHealth(owners.Select(o => new DailySummaryRecipient(o)).ToList()));
    }

    private Task<int> AddReauthConnectionAsync(Guid tenantId) => InTenantAsync(tenantId, db =>
    {
        var connection = new ChannelConnection(tenantId, SalesChannel.MercadoLivre, Random.Shared.Next(1_000_000, 9_999_999).ToString(CultureInfo.InvariantCulture), DateTimeOffset.UtcNow);
        connection.ChangeStatus(ChannelConnectionStatus.ReauthRequired);
        db.ChannelConnections.Add(connection);
        return db.SaveChangesAsync();
    });

    private Task<int> AddFailureAsync(Guid tenantId, string description, string error) => InTenantAsync(tenantId, db =>
    {
        db.MessageFailures.Add(new MessageFailure(tenantId, Guid.NewGuid(), "Teste", description, 3, "HttpRequestException", error, null, DateTimeOffset.UtcNow.AddMinutes(-5)));
        return db.SaveChangesAsync();
    });

    private Task<int> AddFailedCallsAsync(Guid tenantId, int count) => InTenantAsync(tenantId, db =>
    {
        for (var i = 0; i < count; i++)
            db.IntegrationLogs.Add(new IntegrationLogEntry(tenantId, "melhor-envio", "cotar frete", false, 1200, "HTTP 503 Service Unavailable", null, null, null, null,
                DateTimeOffset.UtcNow.AddMinutes(-30 + i)));
        return db.SaveChangesAsync();
    });

    private async Task<SentEmail> EmailAsync(string to, int count = 1)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var emails = Outbox.Sent.Where(e => e.Message.To == to && e.Purpose == Purpose).ToList();
            if (emails.Count >= count) return emails[^1];
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) Assert.Fail($"Alerta não chegou para {to}.");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Problemas_aparecem_no_painel_e_viram_um_alerta_por_e_mail_sem_repetir()
    {
        var tenant = await api.CreateTenantAsync();
        var operatorSession = await api.SessionForAsync(tenant, TenantRole.Operator);
        await AddReauthConnectionAsync(tenant.Id);
        await AddFailureAsync(tenant.Id, "Atualizar anúncio no canal", "HTTP 400: item.attributes.BRAND is required");
        await AddFailedCallsAsync(tenant.Id, 3);

        // Painel: Dono e Operador veem o diagnóstico, com a ação das regras.
        var diagnoses = (await (await GetAsync(api.PanelClient(), operatorSession, "/api/painel/integracoes/diagnostico")).Content.ReadFromJsonAsync<List<DiagnosisDto>>())!;
        Assert.Equal(3, diagnoses.Count);
        Assert.Equal("reconnect", diagnoses.Single(d => d.Kind == "channelReauth").Action);
        Assert.Equal("fixPart", diagnoses.Single(d => d.Kind == "backgroundFailure").Action);
        var calls = diagnoses.Single(d => d.Kind == "repeatedCallFailure");
        Assert.Equal(("waitService", 3, "Melhor Envio: “cotar frete” está falhando"), (calls.Action, calls.Occurrences, calls.Title));

        var to = $"dono-{tenant.Slug}@loja.test";
        Assert.Equal(SyncAlertOutcome.Sent, await CheckAsync(tenant.Id, to));
        var email = await EmailAsync(to);
        Assert.Equal(tenant.Id, email.TenantId);
        Assert.StartsWith("Atenção: 3 problemas na sincronização", email.Message.Subject, StringComparison.Ordinal);
        Assert.Contains("O que fazer: Abra Canais de venda e conecte a conta de novo.", email.Message.Text, StringComparison.Ordinal);
        Assert.Contains("Erro: HTTP 400: item.attributes.BRAND is required", email.Message.Text, StringComparison.Ordinal);
        Assert.Contains("O serviço externo recusou a operação", email.Message.Text, StringComparison.Ordinal); // explicação da IA (simulada) aceita

        // Na rodada seguinte os mesmos problemas não são avisados de novo; um problema novo sai sozinho.
        Assert.Equal(SyncAlertOutcome.Skipped, await CheckAsync(tenant.Id, to));
        await AddFailureAsync(tenant.Id, "Importar venda do Mercado Livre", "HTTP 500");
        Assert.Equal(SyncAlertOutcome.Sent, await CheckAsync(tenant.Id, to));
        var second = await EmailAsync(to, 2);
        Assert.StartsWith("Atenção: problema na sincronização", second.Message.Subject, StringComparison.Ordinal);
        Assert.Contains("Falhou: Importar venda do Mercado Livre", second.Message.Text, StringComparison.Ordinal);
        Assert.Contains("Quando o serviço voltar, clique em Reprocessar em Integrações.", second.Message.Text, StringComparison.Ordinal);

        var alerts = await InTenantAsync(tenant.Id, db => db.SyncAlerts.OrderBy(a => a.SentAt).ToListAsync());
        Assert.Equal((2, 3, 1), (alerts.Count, alerts[0].ProblemKeys.Count, alerts[1].ProblemKeys.Count));
        Assert.True(alerts[0].AiText);
    }

    [Fact]
    public async Task Sem_problema_ou_desligado_nao_avisa_e_so_o_Dono_desliga()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var operatorSession = await api.SessionForAsync(tenant, TenantRole.Operator);
        Assert.Equal(new SettingsDto(true), await (await GetAsync(api.PanelClient(), owner, "/api/painel/guardiao")).Content.ReadFromJsonAsync<SettingsDto>());

        // Duas falhas da mesma chamada ainda não são problema; uma que voltou a dar certo também não.
        await AddFailedCallsAsync(tenant.Id, 2);
        Assert.Equal(SyncAlertOutcome.Skipped, await CheckAsync(tenant.Id, "dono@loja.test"));

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(api.PanelClient(), operatorSession, HttpMethod.Put, "/api/painel/guardiao", new { ativo = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(api.PanelClient(), owner, HttpMethod.Put, "/api/painel/guardiao", new { ativo = false })).StatusCode);
        await AddReauthConnectionAsync(tenant.Id);
        Assert.Equal(SyncAlertOutcome.Skipped, await CheckAsync(tenant.Id, "dono@loja.test"));
        Assert.False(await InTenantAsync(tenant.Id, db => db.SyncAlerts.AnyAsync()));
    }

    [Fact]
    public async Task Agendador_avisa_os_Donos_da_loja_publicada()
    {
        var tenant = await api.CreateTenantAsync();
        await api.SessionForAsync(tenant); // cria o Dono
        var operatorUser = await api.CreateUserAsync(tenant.Id, TenantRole.Operator, "Senha-forte-123");
        var ownerEmail = await api.WithPlatformAsync(db => (from m in db.TenantMemberships
                                                             join u in db.UserAccounts on m.UserId equals u.Id
                                                             where m.TenantId == tenant.Id && m.Role == TenantRole.Owner
                                                             select u.Email).FirstAsync());
        await AddReauthConnectionAsync(tenant.Id);

        var scheduler = ActivatorUtilities.CreateInstance<SyncGuardianScheduler>(api.Services);
        Assert.True(await scheduler.ScheduleAllAsync(CancellationToken.None) >= 1);

        Assert.Contains("Mercado Livre: a conexão perdeu a autorização", (await EmailAsync(ownerEmail)).Message.Text, StringComparison.Ordinal);
        await Task.Delay(500);
        Assert.DoesNotContain(Outbox.Sent, e => e.Message.To == operatorUser.Email && e.Purpose == Purpose);
    }

    [Fact]
    public async Task Cada_loja_ve_so_os_proprios_problemas()
    {
        var tenant = await api.CreateTenantAsync();
        var other = await api.CreateTenantAsync();
        var otherOwner = await api.SessionForAsync(other);
        await AddReauthConnectionAsync(tenant.Id);
        await AddFailureAsync(tenant.Id, "Atualizar anúncio no canal", "HTTP 500");
        Assert.Equal(SyncAlertOutcome.Sent, await CheckAsync(tenant.Id, "a@loja.test"));

        var diagnoses = (await (await GetAsync(api.PanelClient(), otherOwner, "/api/painel/integracoes/diagnostico")).Content.ReadFromJsonAsync<List<DiagnosisDto>>())!;
        Assert.Empty(diagnoses);
        Assert.Equal(SyncAlertOutcome.Skipped, await CheckAsync(other.Id, "b@loja.test"));
        Assert.False(await InTenantAsync(other.Id, db => db.SyncAlerts.IgnoreQueryFilters().AnyAsync()));
    }
}
