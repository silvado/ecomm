using Ecommerce.Application.Channels;
using Ecommerce.Application.Integrations;
using Ecommerce.Domain.Channels;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Platform;
using Ecommerce.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Ecommerce.Infrastructure.Channels.MercadoLivre;

/// <summary>
/// Avisos perdidos do ML (RF21 CA4, RF28): de hora em hora, lê o <c>missed_feeds</c> do app (o ML guarda por 2 dias os
/// avisos que não receberam 200 — ex.: a API estava fora do ar) e os reinjeta no inbox pelo mesmo caminho do webhook.
/// A chave do aviso é a mesma do original, então o que já tinha chegado não se repete. A consulta é do app (vale para
/// todas as lojas) e usa o token de qualquer conexão ativa. Roda só no Worker.
/// Fonte: https://developers.mercadolivre.com.br/pt_br/produto-receba-notificacoes (Histórico das notificações).
/// </summary>
public sealed partial class MercadoLivreMissedFeedsJob(IServiceScopeFactory scopes, IOptions<MercadoLivreOptions> options, TimeProvider clock,
    ILogger<MercadoLivreMissedFeedsJob> logger) : BackgroundService
{
    public static readonly IReadOnlyList<string> Topics = ["orders_v2", "shipments", "items"];
    private const int PageSize = 50;
    private const int MaxPages = 10;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRunDelay, clock, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var recovered = await RunAsync(stoppingToken);
                    if (recovered > 0) LogRecovered(logger, recovered);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    LogFailed(logger, e);
                }
                await Task.Delay(Interval, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Desligando.
        }
    }

    /// <returns>Avisos novos postos no inbox.</returns>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        if (!options.Value.IsConfigured) return 0;
        var appId = string.IsNullOrWhiteSpace(options.Value.ClientId) ? "SIM-APP" : options.Value.ClientId;
        if (await TokenAsync(ct) is not { } token) return 0;

        await using var scope = scopes.CreateAsyncScope();
        var api = scope.ServiceProvider.GetRequiredService<MercadoLivreApiClient>();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        // A origem já é o próprio ML: sem conferir IP, mas o application_id continua conferido.
        var readOptions = new MercadoLivreOptions { ClientId = options.Value.ClientId };
        var recovered = 0;
        foreach (var topic in Topics)
        {
            for (var page = 0; page < MaxPages; page++)
            {
                var messages = await api.MissedFeedsAsync(token, appId, topic, page * PageSize, PageSize, ct);
                foreach (var data in messages)
                {
                    var (check, notification) = MercadoLivreNotifications.Read(data, null, readOptions);
                    if (check != NotificationCheck.Valid || notification is null) continue;
                    var outcome = await bus.InvokeAsync<ReceiveChannelEventOutcome>(new ReceiveChannelEvent(SalesChannel.MercadoLivre, notification.IdempotencyKey,
                        notification.UserId, notification.Topic, notification.Resource, data), ct);
                    if (outcome == ReceiveChannelEventOutcome.Accepted) recovered++;
                }
                if (messages.Count < PageSize) break;
            }
        }
        return recovered;
    }

    /// <summary>Token de alguma conexão ativa do ML (a consulta do missed_feeds é do app).</summary>
    private async Task<string?> TokenAsync(CancellationToken ct)
    {
        await using var lookup = scopes.CreateAsyncScope();
        var tenants = await lookup.ServiceProvider.GetRequiredService<PlatformDbContext>().ChannelAccounts.AsNoTracking()
            .Where(a => a.Channel == SalesChannel.MercadoLivre).Select(a => a.TenantId).Distinct().ToListAsync(ct);
        foreach (var tenantId in tenants)
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
            var connection = await scope.ServiceProvider.GetRequiredService<TenantDbContext>().ChannelConnections.AsNoTracking()
                .Where(c => c.Channel == SalesChannel.MercadoLivre && c.Status == ChannelConnectionStatus.Active).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
            if (connection is not { } id) continue;
            try
            {
                return await scope.ServiceProvider.GetRequiredService<MercadoLivreCredentials>().AccessTokenAsync(id, ct);
            }
            catch (Exception e) when (e is ChannelAuthorizationRevokedException or ChannelConnectorException)
            {
                // Tenta a próxima loja.
            }
        }
        return null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Avisos perdidos do Mercado Livre recuperados: {Count}")]
    private static partial void LogRecovered(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha ao recuperar avisos perdidos do Mercado Livre")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
