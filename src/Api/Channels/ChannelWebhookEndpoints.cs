using Ecommerce.Application.Integrations;
using Ecommerce.Domain.Channels;
using Ecommerce.Infrastructure.Channels.MercadoLivre;
using Ecommerce.Infrastructure.Integrations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Ecommerce.Api.Channels;

/// <param name="Chave">Chave única do aviso no canal (reentrega usa a mesma).</param>
/// <param name="Conta">Conta do vendedor no canal.</param>
public sealed record FakeChannelNotification(string? Chave, string? Conta, string? Topico, string? Recurso);

/// <summary>
/// Webhooks de canais sem loja na URL (RF26): o canal simulado (dev e testes) e o Mercado Livre (RF21 CA4/CA5); Meta e
/// OLX entram no E2/E3 com a validação de cada um. Responde 200 na hora e processa pela fila (regra 7 do CLAUDE.md).
/// </summary>
public static partial class ChannelWebhookEndpoints
{
    private const int MaxBody = 16 * 1024;

    /// <summary>Política de limite dos avisos de canais (o ML desativa os tópicos de quem não responde 200).</summary>
    public const string RateLimit = "webhook-canal";

    /// <summary>
    /// Avisos do Mercado Livre (cadastrar no app do DevCenter como URL de notificações). O ML exige 200 em até 500 ms: aqui só
    /// confere, grava no inbox e responde; o roteamento pelo vendedor (user_id → loja) e o processamento vão pela fila.
    /// </summary>
    public static void MapMercadoLivreWebhook(this IEndpointRouteBuilder app) =>
        app.MapPost($"/api/webhooks/canais/{SalesChannel.MercadoLivre}", async Task<Results<Ok, BadRequest, ForbidHttpResult>> (
            HttpRequest http, IMessageBus bus, IOptions<MercadoLivreOptions> options, ILoggerFactory loggers, CancellationToken ct) =>
        {
            if (http.ContentLength > MaxBody) return TypedResults.BadRequest();
            using var reader = new StreamReader(http.Body);
            var buffer = new char[MaxBody + 1];
            var length = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
            if (length > MaxBody) return TypedResults.BadRequest();
            var body = new string(buffer, 0, length);

            var (check, notification) = MercadoLivreNotifications.Read(body, http.HttpContext.Connection.RemoteIpAddress, options.Value);
            if (check == NotificationCheck.Forbidden)
            {
                LogRefused(loggers.CreateLogger("Ecommerce.Channels.MercadoLivre"), http.HttpContext.Connection.RemoteIpAddress?.ToString());
                return TypedResults.Forbid();
            }
            if (notification is null) return TypedResults.BadRequest();

            // Repetido também responde 200: para o ML, o aviso foi entregue.
            await bus.InvokeAsync<ReceiveChannelEventOutcome>(new ReceiveChannelEvent(SalesChannel.MercadoLivre, notification.IdempotencyKey,
                notification.UserId, notification.Topic, notification.Resource, body), ct);
            return TypedResults.Ok();
        }).RequireRateLimiting(RateLimit).ExcludeFromDescription();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Aviso do Mercado Livre recusado (outro app ou IP fora da lista): {Ip}")]
    private static partial void LogRefused(ILogger logger, string? ip);

    public static void MapFakeChannelWebhook(this IEndpointRouteBuilder app) =>
        app.MapPost($"/api/webhooks/canais/{FakeChannelProcessor.ChannelName}", async Task<Results<Ok, BadRequest>> (
            HttpRequest http, IMessageBus bus, CancellationToken ct) =>
        {
            if (http.ContentLength > MaxBody) return TypedResults.BadRequest();
            FakeChannelNotification? notification;
            try
            {
                notification = await http.ReadFromJsonAsync<FakeChannelNotification>(ct);
            }
            catch (System.Text.Json.JsonException)
            {
                return TypedResults.BadRequest();
            }
            if (notification is null) return TypedResults.BadRequest();
            try
            {
                // Repetido também responde 200: para o canal, o aviso foi entregue.
                await bus.InvokeAsync<ReceiveChannelEventOutcome>(new ReceiveChannelEvent(FakeChannelProcessor.ChannelName, notification.Chave ?? string.Empty,
                    notification.Conta ?? string.Empty, notification.Topico ?? string.Empty, notification.Recurso ?? string.Empty,
                    System.Text.Json.JsonSerializer.Serialize(notification)), ct);
            }
            catch (ArgumentException)
            {
                return TypedResults.BadRequest();
            }
            return TypedResults.Ok();
        }).RequireRateLimiting("webhook").ExcludeFromDescription();
}
