using System.Globalization;
using System.Net;
using Ecommerce.Api.Auth;
using Ecommerce.Api.Panel;
using Ecommerce.Application.Billing;
using Ecommerce.Application.Payments;
using Ecommerce.Domain.Identity;
using Ecommerce.Infrastructure.Billing;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ecommerce.Api.Billing;

public sealed record PayInvoiceResponse(string RedirectUrl);

/// <summary>Assinatura da loja com a plataforma (RF38): tela do Dono, webhook do gateway e — só em dev — o pagamento simulado.</summary>
public static class SubscriptionEndpoints
{
    /// <summary>Tela Assinatura do painel; para onde o gateway devolve o Dono.</summary>
    public const string PanelPath = "/assinatura";

    private const int MaxWebhookBody = 64 * 1024;

    /// <summary>Só quem cuida do plano (Dono).</summary>
    public static void MapSubscriptionEndpoints(this RouteGroupBuilder panel)
    {
        var subscription = panel.MapGroup("/assinatura").RequirePermission(Permission.PlanManage);

        subscription.MapGet("/", async (ISubscriptionPanel service, CancellationToken ct) => TypedResults.Ok(await service.GetAsync(ct)));

        subscription.MapPost("/faturas/{id:guid}/pagar", async Task<Results<Ok<PayInvoiceResponse>, ProblemHttpResult>> (
            Guid id, ISubscriptionPanel service, HttpRequest http, CancellationToken ct) =>
        {
            var result = await service.PayAsync(id, $"{http.Scheme}://{http.Host}{PanelPath}", ct);
            return result.Outcome switch
            {
                PayInvoiceOutcome.Redirect => TypedResults.Ok(new PayInvoiceResponse(result.PaymentUrl!)),
                PayInvoiceOutcome.NotFound => AuthEndpoints.Problem(StatusCodes.Status404NotFound, "Fatura não encontrada."),
                PayInvoiceOutcome.NotPayable => AuthEndpoints.Problem(StatusCodes.Status409Conflict, "Esta fatura já foi paga ou cancelada."),
                _ => AuthEndpoints.Problem(StatusCodes.Status409Conflict,
                    "O pagamento pelo painel ainda não está disponível. Fale com a equipe da plataforma para pagar por Pix ou transferência."),
            };
        });
    }

    /// <summary>Webhook do gateway da plataforma (sem loja na URL: a cobrança diz a fatura). Responde logo (regra 7).</summary>
    public static void MapSubscriptionWebhook(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/webhooks/assinatura/{gateway}", async Task<Results<Ok, UnauthorizedHttpResult, NotFound, BadRequest>> (
            string gateway, HttpRequest http, ISubscriptionWebhook webhook, CancellationToken ct) =>
        {
            if (http.ContentLength > MaxWebhookBody) return TypedResults.BadRequest();
            using var reader = new StreamReader(http.Body);
            var body = await reader.ReadToEndAsync(ct);
            var request = new WebhookRequest(
                http.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString()),
                http.Query.ToDictionary(q => q.Key, q => q.Value.ToString()),
                body.Length > MaxWebhookBody ? string.Empty : body);
            return await webhook.ReceiveAsync(gateway, request, ct) switch
            {
                WebhookOutcome.Accepted or WebhookOutcome.Ignored => TypedResults.Ok(),
                WebhookOutcome.Rejected => TypedResults.Unauthorized(),
                _ => TypedResults.NotFound(),
            };
        }).RequireRateLimiting("webhook").ExcludeFromDescription();

    /// <summary>
    /// Só com o gateway simulado: a "página do gateway" onde o Dono paga. O aviso é assinado e entra pelo mesmo caminho do
    /// webhook real; o valor vem da cobrança (consulta), não da página.
    /// </summary>
    public static void MapFakeSubscriptionPayment(this IEndpointRouteBuilder app)
    {
        var simulation = app.MapGroup(FakeSubscriptionBilling.SimulationPath + "/{chargeId}").ExcludeFromDescription();

        simulation.MapGet("/", async Task<Results<ContentHttpResult, BadRequest>> (string chargeId, string volta, ISubscriptionBilling billing, CancellationToken ct) =>
        {
            if (ReturnUrl(volta) is null || await billing.GetChargeAsync(chargeId, ct) is not { Status: ChargeStatus.Pending }) return TypedResults.BadRequest();
            var amount = chargeId.Split('.')[2];
            var value = (long.Parse(amount, CultureInfo.InvariantCulture) / 100m).ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');
            var action = WebUtility.HtmlEncode($"{FakeSubscriptionBilling.SimulationPath}/{chargeId}/pagar?volta={Uri.EscapeDataString(volta)}");
            return TypedResults.Content($"""
                <!doctype html><html lang="pt-BR"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
                <title>Cobrança simulada</title><meta name="robots" content="noindex"></head>
                <body style="font-family:system-ui;max-width:420px;margin:3rem auto;padding:0 1rem">
                <h1>Cobrança simulada</h1><p>Ambiente de desenvolvimento: nenhum dinheiro é movimentado.</p>
                <p>Assinatura da plataforma · <strong>R$ {value}</strong></p>
                <form method="post" action="{action}"><button style="width:100%;min-height:48px">Pagar</button></form>
                <p><a href="{WebUtility.HtmlEncode(volta)}">Voltar sem pagar</a></p>
                </body></html>
                """, "text/html; charset=utf-8");
        });

        simulation.MapPost("/pagar", async Task<Results<RedirectHttpResult, BadRequest>> (
            string chargeId, string volta, ISubscriptionBilling billing, IServiceScopeFactory scopes, CancellationToken ct) =>
        {
            if (ReturnUrl(volta) is not { } back || await billing.GetChargeAsync(chargeId, ct) is not { Status: ChargeStatus.Pending })
                return TypedResults.BadRequest();
            var paid = FakeSubscriptionBilling.PaidChargeId(chargeId);
            var requestId = Guid.NewGuid().ToString();
            var notification = new WebhookRequest(
                new Dictionary<string, string> { ["x-request-id"] = requestId, [FakeSubscriptionBilling.SignatureHeader] = FakeSubscriptionBilling.Sign(paid, requestId) },
                new Dictionary<string, string> { ["evento"] = "cobranca", ["id"] = paid },
                string.Empty);
            await using (var scope = scopes.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<ISubscriptionWebhook>().ReceiveAsync(FakeSubscriptionBilling.GatewayName, notification, ct);
            return TypedResults.Redirect(back);
        });
    }

    /// <summary>Volta só para a tela Assinatura do painel (evita redirecionamento aberto).</summary>
    private static string? ReturnUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.AbsolutePath == PanelPath ? uri.ToString() : null;
}
