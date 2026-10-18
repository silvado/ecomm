using System.Diagnostics;
using Ecommerce.Application.Reports;
using Ecommerce.Application.Vault;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Vault;
using Ecommerce.Infrastructure.Email;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Ecommerce.Infrastructure.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF06 CA5 — aviso de vencimento do certificado A1 por e-mail aos Donos nos dias de aviso: uma vez por dia de aviso
/// (rodar de novo não repete), "vencido" depois da validade, certificado trocado no meio não gera aviso velho, Operador
/// não recebe e cada loja só registra os seus avisos.
/// </summary>
public sealed class CertificateReminderTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private const string Purpose = "aviso do certificado digital";

    private InMemoryEmailOutbox Outbox => api.Services.GetRequiredService<InMemoryEmailOutbox>();

    private async Task<T> InTenantAsync<T>(Guid tenantId, Func<TenantDbContext, Task<T>> query)
    {
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        return await query(scope.ServiceProvider.GetRequiredService<TenantDbContext>());
    }

    /// <summary>Certificado no cofre só com a validade (o aviso não lê o conteúdo).</summary>
    private Task<DateTimeOffset> SetCertificateAsync(Guid tenantId, double days) => InTenantAsync(tenantId, async db =>
    {
        var at = DateTimeOffset.UtcNow.AddDays(days);
        at = new DateTimeOffset(at.UtcTicks - at.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero); // precisão do timestamptz
        var secret = await db.TenantSecrets.SingleOrDefaultAsync(s => s.Kind == SecretKind.A1Certificate && s.Name == VaultPanel.CertificateName);
        if (secret is null) db.TenantSecrets.Add(new TenantSecret(tenantId, SecretKind.A1Certificate, VaultPanel.CertificateName, [1, 2, 3], "v1", "A1", at, DateTimeOffset.UtcNow));
        else secret.Replace([1, 2, 3], "v1", "A1", at, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        return at;
    });

    private async Task<string> OwnerEmailAsync(Guid tenantId) =>
        await api.WithPlatformAsync(db => (from m in db.TenantMemberships
                                           join u in db.UserAccounts on m.UserId equals u.Id
                                           where m.TenantId == tenantId && m.Role == TenantRole.Owner
                                           select u.Email).FirstAsync());

    private async Task<List<SentEmail>> EmailsAsync(string to, int count)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var emails = Outbox.Sent.Where(e => e.Message.To == to && e.Purpose == Purpose).ToList();
            if (emails.Count >= count) return emails;
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) Assert.Fail($"Aviso não chegou para {to} ({emails.Count} de {count}).");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Dono_recebe_o_aviso_uma_vez_por_dia_de_aviso_e_o_de_vencido()
    {
        var tenant = await api.CreateTenantAsync();
        await api.SessionForAsync(tenant); // cria o Dono
        var operatorUser = await api.CreateUserAsync(tenant.Id, TenantRole.Operator, "Senha-forte-123");
        var owner = await OwnerEmailAsync(tenant.Id);
        var monitor = ActivatorUtilities.CreateInstance<CertificateExpiryMonitor>(api.Services);

        await SetCertificateAsync(tenant.Id, 15.5);
        await monitor.CheckAsync(CancellationToken.None);
        var first = Assert.Single(await EmailsAsync(owner, 1));
        Assert.StartsWith("Certificado digital vence em 15 dias", first.Message.Subject, StringComparison.Ordinal);
        Assert.Contains("Cofre → Certificado digital", first.Message.Text, StringComparison.Ordinal);
        Assert.Equal(tenant.Id, first.TenantId);

        // O Worker reiniciou no mesmo dia: o aviso não sai de novo.
        await monitor.CheckAsync(CancellationToken.None);
        await Task.Delay(500);
        Assert.Single(Outbox.Sent, e => e.Message.To == owner && e.Purpose == Purpose);

        await SetCertificateAsync(tenant.Id, -0.5);
        await monitor.CheckAsync(CancellationToken.None);
        var expired = (await EmailsAsync(owner, 2))[^1];
        Assert.StartsWith("Certificado digital vencido", expired.Message.Subject, StringComparison.Ordinal);
        Assert.Contains("está parada", expired.Message.Text, StringComparison.Ordinal);

        Assert.DoesNotContain(Outbox.Sent, e => e.Message.To == operatorUser.Email && e.Purpose == Purpose);
        Assert.Equal(2, await InTenantAsync(tenant.Id, db => db.CertificateReminders.CountAsync()));
    }

    [Fact]
    public async Task Fora_do_dia_de_aviso_ou_com_certificado_trocado_nao_avisa()
    {
        var tenant = await api.CreateTenantAsync();
        await api.SessionForAsync(tenant);
        var owner = await OwnerEmailAsync(tenant.Id);
        var monitor = ActivatorUtilities.CreateInstance<CertificateExpiryMonitor>(api.Services);

        await SetCertificateAsync(tenant.Id, 20.5);
        Assert.DoesNotContain(await monitor.CheckAsync(CancellationToken.None), r => r.TenantId == tenant.Id);

        // Pedido atrasado sobre um certificado que já foi substituído: não avisa.
        var old = await SetCertificateAsync(tenant.Id, 5.5);
        await SetCertificateAsync(tenant.Id, 300);
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var outcome = await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeForTenantAsync<CertificateReminderOutcome>(tenant.Id.ToString(),
                new SendCertificateReminder(old, 5, [new DailySummaryRecipient(owner)]));
            Assert.Equal(CertificateReminderOutcome.Skipped, outcome);
        }
        await Task.Delay(500);
        Assert.DoesNotContain(Outbox.Sent, e => e.Message.To == owner && e.Purpose == Purpose);
        Assert.False(await InTenantAsync(tenant.Id, db => db.CertificateReminders.AnyAsync()));
    }

    [Fact]
    public async Task Cada_loja_registra_so_os_proprios_avisos()
    {
        var tenant = await api.CreateTenantAsync();
        var other = await api.CreateTenantAsync();
        await api.SessionForAsync(tenant);
        await SetCertificateAsync(tenant.Id, 30.5);
        await ActivatorUtilities.CreateInstance<CertificateExpiryMonitor>(api.Services).CheckAsync(CancellationToken.None);
        await EmailsAsync(await OwnerEmailAsync(tenant.Id), 1);

        Assert.Equal(1, await InTenantAsync(tenant.Id, db => db.CertificateReminders.CountAsync()));
        Assert.False(await InTenantAsync(other.Id, db => db.CertificateReminders.IgnoreQueryFilters().AnyAsync()));
    }
}
