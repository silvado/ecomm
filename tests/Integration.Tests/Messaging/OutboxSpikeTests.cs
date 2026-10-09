using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Ecommerce.Integration.Tests.Messaging;

/// <summary>
/// Spike ADR-0002 (PBI "Spike: Wolverine + EF Core + RLS").
/// Perguntas: o outbox é transacional com o EF? o tenant chega ao handler? o RLS vale no handler? qual a latência?
/// </summary>
public sealed class OutboxSpikeTests(MessagingFixture fx, ITestOutputHelper output) : IClassFixture<MessagingFixture>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Mensagem_publicada_pelo_outbox_chega_ao_handler_no_escopo_do_tenant()
    {
        var probeId = await PublishAsync(fx.TenantA, fx.PartA, commit: true);

        var seen = await fx.Probe.WaitForAsync(probeId, Timeout);

        Assert.NotNull(seen);
        Assert.Equal(fx.TenantA, seen.TenantInContext);
        Assert.Equal(fx.TenantA, seen.DbContextTenant);
        Assert.Equal(fx.TenantA.ToString(), seen.DbTenantSetting);
        Assert.Equal([fx.TenantA], seen.VisibleTenants); // RLS ativo dentro do handler
        Assert.True(seen.PartFound);
    }

    [Fact]
    public async Task Handler_do_tenant_B_nao_enxerga_dados_do_tenant_A()
    {
        var probeId = await PublishAsync(fx.TenantB, fx.PartA, commit: true); // peça de A, mensagem de B

        var seen = await fx.Probe.WaitForAsync(probeId, Timeout);

        Assert.NotNull(seen);
        Assert.Equal([fx.TenantB], seen.VisibleTenants);
        Assert.False(seen.PartFound);
    }

    [Fact]
    public async Task Mensagem_sem_tenant_nao_enxerga_nenhum_dado()
    {
        var probeId = await PublishAsync(tenantId: null, fx.PartA, commit: true);

        var seen = await fx.Probe.WaitForAsync(probeId, Timeout);

        Assert.NotNull(seen);
        Assert.Null(seen.TenantInContext);
        Assert.Empty(seen.VisibleTenants);
    }

    [Fact]
    public async Task Transacao_nao_confirmada_nao_entrega_mensagem()
    {
        var probeId = await PublishAsync(fx.TenantA, fx.PartA, commit: false);

        var seen = await fx.Probe.WaitForAsync(probeId, TimeSpan.FromSeconds(3));

        Assert.Null(seen);
    }

    [Fact]
    public async Task Gravacao_do_handler_e_persistida_pela_transacao_do_Wolverine()
    {
        var probeId = await PublishAsync(fx.TenantA, fx.PartA, commit: true);
        Assert.NotNull(await fx.Probe.WaitForAsync(probeId, Timeout));

        // O probe é registrado dentro do handler, antes de o Wolverine confirmar a transação (após o handler):
        // a gravação aparece logo depois — esperar por ela em vez de ler uma vez só.
        // Outras mensagens podem renomear a mesma peça depois; basta que alguma gravação do handler tenha persistido.
        string? title = null;
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var scope = fx.Host.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantScope>().Set(fx.TenantA);
            var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
            title = await db.Parts.Where(p => p.Id == fx.PartA).Select(p => p.Title).SingleAsync();
            if (title.StartsWith("Processado ", StringComparison.Ordinal)) break;
            await Task.Delay(20);
        }
        Assert.StartsWith("Processado ", title);
    }

    [Fact]
    public async Task Latencia_entre_commit_e_handler_fica_muito_abaixo_de_60s()
    {
        const int count = 50;
        var latencies = new List<TimeSpan>();
        for (var i = 0; i < count; i++)
        {
            var probeId = await PublishAsync(fx.TenantA, fx.PartA, commit: true);
            var seen = await fx.Probe.WaitForAsync(probeId, Timeout);
            Assert.NotNull(seen);
            latencies.Add(seen.Latency);
        }

        latencies.Sort();
        var p50 = latencies[count / 2];
        var p95 = latencies[(int)(count * 0.95) - 1];
        var max = latencies[^1];
        output.WriteLine($"Latência commit→handler ({count} msgs): p50={p50.TotalMilliseconds:F0} ms, p95={p95.TotalMilliseconds:F0} ms, máx={max.TotalMilliseconds:F0} ms");

        Assert.True(p95 < TimeSpan.FromSeconds(5), $"p95 = {p95}");
    }

    /// <summary>Simula um caso de uso: altera a peça e publica o evento na mesma transação.</summary>
    private async Task<Guid> PublishAsync(Guid? tenantId, Guid partId, bool commit)
    {
        var probeId = Guid.CreateVersion7();
        await using var scope = fx.Host.Services.CreateAsyncScope();
        if (tenantId is { } id) scope.ServiceProvider.GetRequiredService<TenantScope>().Set(id);

        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox<TenantDbContext>>();
        var part = await outbox.DbContext.Parts.SingleOrDefaultAsync(p => p.Id == partId);
        part?.Rename($"Publicado {probeId}");

        var options = new DeliveryOptions { TenantId = tenantId?.ToString() };
        await outbox.PublishAsync(new PartChanged(partId, probeId, DateTimeOffset.UtcNow), options);

        if (!commit) return probeId; // descarta sem SaveChanges

        await outbox.SaveChangesAndFlushMessagesAsync();
        return probeId;
    }
}
