using Ecommerce.Application.Vault;
using Ecommerce.Domain.Vault;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Platform;
using Ecommerce.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace Ecommerce.Infrastructure.Vault;

/// <summary>
/// Vencimento do certificado A1 (RF06 CA5): uma vez por dia, loja a loja, registra no log 30, 15 e 5 dias antes, no dia
/// em que vence e no dia seguinte, e pede o e-mail aos Donos das lojas publicadas e não suspensas (que sai uma vez por dia
/// de aviso — <see cref="CertificateReminderHandler"/>). O painel mostra a faixa de aviso a partir de 30 dias. Só no Worker.
/// </summary>
public sealed partial class CertificateExpiryMonitor(IServiceScopeFactory scopes, TimeProvider clock, ILogger<CertificateExpiryMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRunDelay, clock, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await CheckAsync(stoppingToken);
                await Task.Delay(Interval, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Desligando.
        }
    }

    /// <summary>Confere todas as lojas; devolve os avisos registrados (loja e dias restantes).</summary>
    public async Task<IReadOnlyList<(Guid TenantId, int DaysLeft)>> CheckAsync(CancellationToken ct)
    {
        List<(Guid Id, string Slug)> tenants;
        IReadOnlyDictionary<Guid, IReadOnlyList<Application.Reports.DailySummaryRecipient>> owners;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            tenants = (await platform.Tenants.AsNoTracking().Select(t => new { t.Id, t.Slug }).ToListAsync(ct)).Select(t => (t.Id, t.Slug)).ToList();
            owners = await Reports.StoreOwners.LoadAsync(platform, ct);
        }

        var now = clock.GetUtcNow();
        var reminders = new List<(Guid TenantId, int DaysLeft)>();
        foreach (var (tenantId, slug) in tenants)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
                var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
                var expiresAt = await db.TenantSecrets.AsNoTracking()
                    .Where(s => s.Kind == SecretKind.A1Certificate && s.Name == VaultPanel.CertificateName)
                    .Select(s => s.ExpiresAt).SingleOrDefaultAsync(ct);
                if (expiresAt is not { } at) continue;
                var days = CertificateAlerts.DaysLeft(at, now);
                if (!CertificateAlerts.IsReminderDay(days)) continue;
                if (at <= now) LogExpired(logger, slug, tenantId, at);
                else LogExpiring(logger, slug, tenantId, days, at);
                reminders.Add((tenantId, days));
                if (owners.TryGetValue(tenantId, out var recipients))
                    await scope.ServiceProvider.GetRequiredService<IMessageBus>().PublishAsync(new SendCertificateReminder(at, days, recipients),
                        new DeliveryOptions { TenantId = tenantId.ToString() });
            }
            catch (Exception e) when (e is DbUpdateException or Npgsql.NpgsqlException or InvalidOperationException)
            {
                LogCheckFailed(logger, e, tenantId);
            }
        }
        return reminders;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Certificado A1 da loja {Slug} ({TenantId}) vence em {Days} dias ({ExpiresAt:yyyy-MM-dd})")]
    private static partial void LogExpiring(ILogger logger, string slug, Guid tenantId, int days, DateTimeOffset expiresAt);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Certificado A1 da loja {Slug} ({TenantId}) venceu em {ExpiresAt:yyyy-MM-dd}: a NF-e pela plataforma para")]
    private static partial void LogExpired(ILogger logger, string slug, Guid tenantId, DateTimeOffset expiresAt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Verificação do certificado A1 falhou na loja {TenantId}")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception, Guid tenantId);
}
