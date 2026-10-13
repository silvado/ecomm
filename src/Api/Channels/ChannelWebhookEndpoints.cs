using Ecommerce.Application.Integrations;
using Ecommerce.Infrastructure.Integrations;
using Microsoft.AspNetCore.Http.HttpResults;
using Wolverine;

namespace Ecommerce.Api.Channels;

/// <param name="Chave">Chave única do aviso no canal (reentrega usa a mesma).</param>
/// <param name="Conta">Conta do vendedor no canal.</param>
public sealed record FakeChannelNotification(string? Chave, string? Conta, string? Topico, string? Recurso);

/// <summary>
/// Webhooks de canais sem loja na URL (RF26). Por ora só o canal simulado (dev e testes); os reais (Mercado Livre, Meta,
/// OLX) entram no E2/E3 com a validação de cada um. Responde 200 na hora e processa pela fila (regra 7 do CLAUDE.md).
/// </summary>
public static class ChannelWebhookEndpoints
{
    private const int MaxBody = 16 * 1024;

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
