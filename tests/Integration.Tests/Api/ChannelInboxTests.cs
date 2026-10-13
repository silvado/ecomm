using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Ecommerce.Domain.Integrations;
using Ecommerce.Domain.Payments;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Integrations;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF26 — avisos de canais sem loja na URL: inbox com chave única, roteamento pelo mapa de contas (banco plataforma),
/// efeito único na loja mesmo com 10 reentregas simultâneas, aviso sem dono e o schema inbox sem abrir as tabelas de loja.
/// </summary>
public sealed class ChannelInboxTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private const string Webhook = "/api/webhooks/canais/" + FakeChannelProcessor.ChannelName;

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    private async Task<(Tenant Tenant, string Account)> ConnectedTenantAsync()
    {
        var tenant = await api.CreateTenantAsync();
        var account = "vendedor-" + NewId();
        await api.WithPlatformAsync(async db =>
        {
            db.ChannelAccounts.Add(new ChannelAccount(tenant.Id, FakeChannelProcessor.ChannelName, account, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        });
        return (tenant, account);
    }

    private Task<HttpResponseMessage> NotifyAsync(string key, string account, string topic = "pedido", string resource = "/orders/1") =>
        api.ClientFor("api.plataforma.test", $"198.51.107.{Random.Shared.Next(1, 250)}")
            .PostAsJsonAsync(Webhook, new { chave = key, conta = account, topico = topic, recurso = resource });

    private async Task<T> InTenantAsync<T>(Guid? tenantId, Func<TenantDbContext, Task<T>> query)
    {
        await using var scope = api.Services.CreateAsyncScope();
        if (tenantId is { } id) scope.ServiceProvider.GetRequiredService<TenantScope>().Set(id);
        return await query(scope.ServiceProvider.GetRequiredService<TenantDbContext>());
    }

    private async Task<InboxEvent> WaitInboxAsync(string key, Func<InboxEvent, bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var row = await InTenantAsync(null, db => db.InboxEvents.AsNoTracking().SingleOrDefaultAsync(e => e.IdempotencyKey == key));
            if (row is not null && done(row)) return row;
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) Assert.Fail($"Aviso {key} ainda {row?.Status.ToString() ?? "não gravado"}.");
            await Task.Delay(100);
        }
    }

    private async Task<ExternalEvent> WaitExternalAsync(Guid tenantId, string key, Func<ExternalEvent, bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var row = await InTenantAsync(tenantId, db => db.ExternalEvents.AsNoTracking().SingleOrDefaultAsync(e => e.IdempotencyKey == key));
            if (row is not null && done(row)) return row;
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) Assert.Fail($"Evento {key} ainda {row?.Status.ToString() ?? "não registrado"} na loja.");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Dez_reentregas_simultaneas_geram_um_aviso_e_um_efeito_na_loja()
    {
        var (tenant, account) = await ConnectedTenantAsync();
        var key = "pedido-" + NewId();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => NotifyAsync(key, account, resource: $"/orders/{key}")));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        var inbox = await WaitInboxAsync(key, e => e.Status == InboxStatus.Routed);
        Assert.Equal(tenant.Id, inbox.TenantId);
        await WaitExternalAsync(tenant.Id, key, e => e.Status == ExternalEventStatus.Processed);

        // Mais reentregas depois de processado também não repetem nada.
        await NotifyAsync(key, account, resource: $"/orders/{key}");
        await Task.Delay(1000);
        Assert.Equal(1, await InTenantAsync(null, db => db.InboxEvents.CountAsync(e => e.IdempotencyKey == key)));
        Assert.Equal(1, await InTenantAsync(tenant.Id, db => db.ExternalEvents.CountAsync(e => e.IdempotencyKey == key)));
        Assert.Equal(1, await InTenantAsync(tenant.Id, db => db.IntegrationLogs.CountAsync(l => l.Reference == $"/orders/{key}")));
    }

    [Fact]
    public async Task Aviso_vai_so_para_a_loja_dona_da_conta()
    {
        var (shopA, accountA) = await ConnectedTenantAsync();
        var (shopB, _) = await ConnectedTenantAsync();
        var key = "pedido-" + NewId();

        await NotifyAsync(key, accountA);
        await WaitExternalAsync(shopA.Id, key, e => e.Status == ExternalEventStatus.Processed);

        Assert.False(await InTenantAsync(shopB.Id, db => db.ExternalEvents.IgnoreQueryFilters().AnyAsync(e => e.IdempotencyKey == key)));
        Assert.False(await InTenantAsync(shopB.Id, db => db.IntegrationLogs.IgnoreQueryFilters().AnyAsync(l => l.Channel == FakeChannelProcessor.ChannelName)));
    }

    [Fact]
    public async Task Conta_sem_loja_fica_sem_dono_e_nada_entra_em_loja_alguma()
    {
        var key = "pedido-" + NewId();
        await NotifyAsync(key, "conta-desconhecida-" + NewId());

        var inbox = await WaitInboxAsync(key, e => e.Status != InboxStatus.Pending);
        Assert.Equal((InboxStatus.Unroutable, (Guid?)null), (inbox.Status, inbox.TenantId));
    }

    [Fact]
    public async Task Topico_que_o_canal_nao_trata_fica_ignorado_e_aviso_malformado_e_recusado()
    {
        var (tenant, account) = await ConnectedTenantAsync();
        var key = "outro-" + NewId();
        await NotifyAsync(key, account, topic: "ignorar");
        await WaitExternalAsync(tenant.Id, key, e => e.Status == ExternalEventStatus.Ignored);

        Assert.Equal(HttpStatusCode.BadRequest, (await NotifyAsync("", account)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await NotifyAsync(new string('x', 300), account)).StatusCode);
    }

    [Fact]
    public async Task Usuario_da_aplicacao_le_o_inbox_mas_nao_ve_tabela_de_loja_nem_cria_objetos_no_schema()
    {
        var (tenant, account) = await ConnectedTenantAsync();
        var key = "pedido-" + NewId();
        await NotifyAsync(key, account);
        await WaitExternalAsync(tenant.Id, key, e => e.Status == ExternalEventStatus.Processed);

        await using var connection = new NpgsqlConnection(api.TenantsConnectionString);
        await connection.OpenAsync();
        async Task<long> Count(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        Assert.True(await Count("SELECT count(*) FROM inbox.external_events") > 0);
        Assert.Equal(0, await Count("SELECT count(*) FROM public.external_events")); // sem loja definida: RLS fecha
        await using var create = new NpgsqlCommand("CREATE TABLE inbox.intrusa (id int)", connection);
        var denied = await Assert.ThrowsAsync<PostgresException>(() => create.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
    }

    [Fact]
    public async Task Limpeza_tira_roteados_antigos_e_guarda_sem_dono_por_90_dias()
    {
        var (_, account) = await ConnectedTenantAsync();
        var routed = "velho-" + NewId();
        var orphan = "orfao-" + NewId();
        await NotifyAsync(routed, account);
        await NotifyAsync(orphan, "ninguem-" + NewId());
        await WaitInboxAsync(routed, e => e.Status == InboxStatus.Routed);
        await WaitInboxAsync(orphan, e => e.Status == InboxStatus.Unroutable);
        await InTenantAsync(null, db => db.Database.ExecuteSqlAsync(
            $"UPDATE inbox.external_events SET received_at = now() - interval '40 days' WHERE idempotency_key IN ({routed}, {orphan})"));

        await ActivatorUtilities.CreateInstance<IntegrationLogRetention>(api.Services).PurgeAsync(CancellationToken.None);

        Assert.False(await InTenantAsync(null, db => db.InboxEvents.AnyAsync(e => e.IdempotencyKey == routed)));
        Assert.True(await InTenantAsync(null, db => db.InboxEvents.AnyAsync(e => e.IdempotencyKey == orphan)));
    }
}
