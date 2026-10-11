using System.Globalization;
using System.Net;
using Ecommerce.Api.Auth;
using Ecommerce.Api.Panel;
using Ecommerce.Application.Payments;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Payments;
using Ecommerce.Infrastructure.Payments;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ecommerce.Api.Payments;

internal sealed record StartPaymentRequest(string? Token);

internal sealed record StartPaymentResponse(string RedirectUrl);

/// <summary>Pagamento online (RF15): checkout no gateway, webhook e — só em dev — a página de pagamento simulado.</summary>
internal static class PaymentEndpoints
{
    private const int MaxWebhookBody = 64 * 1024;

    /// <summary>Na loja (tenant pelo Host): leva o comprador ao checkout do gateway.</summary>
    public static void MapStorePayment(this RouteGroupBuilder store) =>
        store.MapPost("/pedidos/pagamento", async Task<Results<Ok<StartPaymentResponse>, ProblemHttpResult, NotFound>> (
            StartPaymentRequest request, IStorefrontPayment payments, HttpRequest http, CancellationToken ct) =>
        {
            var result = await payments.StartAsync(request.Token, $"{http.Scheme}://{http.Host}", ct);
            return result.Outcome switch
            {
                StartPaymentOutcome.Redirect => TypedResults.Ok(new StartPaymentResponse(result.RedirectUrl!)),
                StartPaymentOutcome.NotFound => TypedResults.NotFound(),
                _ => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: result.Message),
            };
        }).RequireRateLimiting("pedido");

    /// <summary>No painel, só para quem administra o cofre (Dono): credenciais do Mercado Pago, nunca devolvidas (RF06 CA3).</summary>
    public static void MapPaymentSettings(this RouteGroupBuilder panel)
    {
        var settings = panel.MapGroup("/pagamentos/mercado-pago").RequirePermission(Permission.VaultManage);

        settings.MapGet("/", async (IPaymentSettings payments, CancellationToken ct) => TypedResults.Ok(await payments.GetMercadoPagoAsync(ct)));

        settings.MapPut("/", async Task<Results<Ok<MercadoPagoSettings>, ProblemHttpResult>> (
            SaveMercadoPagoCredentials request, IPaymentSettings payments, CancellationToken ct) =>
        {
            var result = await payments.SaveMercadoPagoAsync(request, ct);
            return result.Settings is { } saved ? TypedResults.Ok(saved) : AuthEndpoints.Problem(StatusCodes.Status400BadRequest, result.Error!);
        });

        settings.MapDelete("/", async (IPaymentSettings payments, CancellationToken ct) => TypedResults.Ok(await payments.RemoveMercadoPagoAsync(ct)));
    }

    /// <summary>
    /// Webhook do gateway, com a loja na URL (cada loja tem a própria conta e chave de assinatura). Responde logo:
    /// a consulta ao gateway e a baixa vão para a fila (regra 7 do CLAUDE.md).
    /// </summary>
    public static void MapPaymentWebhooks(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/webhooks/pagamentos/{gateway}/{tenantId:guid}", async Task<Results<Ok, UnauthorizedHttpResult, NotFound, BadRequest>> (
            string gateway, Guid tenantId, HttpRequest http, IPaymentWebhook webhook, CancellationToken ct) =>
        {
            if (http.ContentLength > MaxWebhookBody) return TypedResults.BadRequest();
            using var reader = new StreamReader(http.Body);
            var body = await reader.ReadToEndAsync(ct);
            var request = new WebhookRequest(
                http.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString()),
                http.Query.ToDictionary(q => q.Key, q => q.Value.ToString()),
                body.Length > MaxWebhookBody ? string.Empty : body);
            return await webhook.ReceiveAsync(tenantId, gateway, request, ct) switch
            {
                WebhookOutcome.Accepted or WebhookOutcome.Ignored => TypedResults.Ok(),
                WebhookOutcome.Rejected => TypedResults.Unauthorized(),
                _ => TypedResults.NotFound(),
            };
        }).ExcludeFromDescription();

    /// <summary>
    /// Só com o gateway fake: página do "gateway" onde o comprador aprova ou recusa. A notificação é assinada e entra
    /// pelo mesmo caminho do webhook real; o valor vem da URL, e um valor adulterado cai na conferência do total.
    /// </summary>
    public static void MapFakePaymentSimulation(this IEndpointRouteBuilder app)
    {
        var simulation = app.MapGroup(FakePaymentGateway.SimulationPath + "/{tenantId:guid}/{orderId:guid}").ExcludeFromDescription();

        simulation.MapGet("/", Results<ContentHttpResult, BadRequest> (Guid tenantId, Guid orderId, long numero, long total, string volta) =>
        {
            if (ReturnUrl(volta) is null || total <= 0) return TypedResults.BadRequest();
            var amount = (total / 100m).ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');
            var query = string.Create(CultureInfo.InvariantCulture, $"total={total}&volta={Uri.EscapeDataString(volta)}");
            var action = WebUtility.HtmlEncode($"{FakePaymentGateway.SimulationPath}/{tenantId}/{orderId}");
            var q = WebUtility.HtmlEncode(query);
            return TypedResults.Content($"""
                <!doctype html><html lang="pt-BR"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
                <title>Pagamento simulado</title><meta name="robots" content="noindex"></head>
                <body style="font-family:system-ui;max-width:420px;margin:3rem auto;padding:0 1rem">
                <h1>Pagamento simulado</h1><p>Ambiente de desenvolvimento: nenhum dinheiro é movimentado.</p>
                <p>Pedido nº {numero} · <strong>R$ {amount}</strong></p>
                <form method="post" action="{action}/aprovar?{q}"><button style="width:100%;min-height:48px">Aprovar pagamento</button></form>
                <form method="post" action="{action}/recusar?{q}" style="margin-top:.75rem"><button style="width:100%;min-height:48px">Recusar pagamento</button></form>
                </body></html>
                """, "text/html; charset=utf-8");
        });

        simulation.MapPost("/{decision}", async Task<Results<RedirectHttpResult, BadRequest>> (
            Guid tenantId, Guid orderId, string decision, long total, string volta, IServiceScopeFactory scopes, CancellationToken ct) =>
        {
            if (ReturnUrl(volta) is not { } back || total <= 0 || decision is not ("aprovar" or "recusar")) return TypedResults.BadRequest();
            var paymentId = FakePaymentGateway.PaymentId(orderId, decision == "aprovar" ? PaymentStatus.Approved : PaymentStatus.Rejected, total / 100m);

            var requestId = Guid.NewGuid().ToString();
            var notification = new WebhookRequest(
                new Dictionary<string, string> { ["x-request-id"] = requestId, [FakePaymentGateway.SignatureHeader] = FakePaymentGateway.Sign(paymentId, requestId) },
                new Dictionary<string, string> { ["type"] = "payment", ["data.id"] = paymentId },
                string.Empty);
            await using (var scope = scopes.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<IPaymentWebhook>().ReceiveAsync(tenantId, FakePaymentGateway.GatewayName, notification, ct);
            return TypedResults.Redirect(back);
        });
    }

    /// <summary>Volta só para a página de pedido de uma loja (evita redirecionamento aberto).</summary>
    private static string? ReturnUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.AbsolutePath == "/pedido" ? uri.ToString() : null;
}
