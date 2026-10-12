using Ecommerce.Api.Auth;
using Ecommerce.Application.Email;
using Ecommerce.Domain.Identity;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ecommerce.Api.Panel;

public sealed record EmailTestRequest(string? Destinatario);

/// <summary>
/// E-mail de envio da loja (Q38) em Dados da loja. Só para quem administra o cofre (Dono): a senha do SMTP vai para lá e
/// nunca volta.
/// </summary>
public static class EmailEndpoints
{
    public static void MapEmailSettings(this RouteGroupBuilder panel)
    {
        var email = panel.MapGroup("/loja/email").RequirePermission(Permission.VaultManage);

        email.MapGet("/", async (IStoreEmailSettings settings, CancellationToken ct) => TypedResults.Ok(await settings.GetAsync(ct)));

        email.MapPut("/", async Task<Results<Ok<StoreEmailSettingsView>, ProblemHttpResult>> (
            SaveStoreEmailSettings request, IStoreEmailSettings settings, CancellationToken ct) =>
        {
            try
            {
                return TypedResults.Ok(await settings.SaveAsync(request, ct));
            }
            catch (ArgumentException e)
            {
                return AuthEndpoints.Problem(StatusCodes.Status400BadRequest, e.Message.Split(" (Parameter", 2)[0]);
            }
        });

        // Envio na hora (sem fila), para o Dono ver o erro do servidor de e-mail enquanto configura.
        email.MapPost("/teste", async Task<Results<Ok<EmailTestResult>, ProblemHttpResult>> (
            EmailTestRequest request, IStoreEmailSettings settings, CancellationToken ct) =>
        {
            var result = await settings.SendTestAsync(request.Destinatario ?? string.Empty, ct);
            return result.Outcome == EmailTestOutcome.Invalid
                ? AuthEndpoints.Problem(StatusCodes.Status400BadRequest, result.Message!)
                : TypedResults.Ok(result);
        }).RequireRateLimiting(EmailTestRateLimit);
    }

    public const string EmailTestRateLimit = "email-teste";
}
