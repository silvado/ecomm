using Ecommerce.Api.Auth;
using Ecommerce.Application.Vault;
using Ecommerce.Domain.Identity;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ecommerce.Api.Panel;

/// <param name="PfxBase64">Arquivo .pfx/.p12 em base64.</param>
public sealed record CertificateRequest(string? PfxBase64, string? Password);

/// <summary>
/// Cofre no painel (RF06), só para o Dono: metadados dos segredos (CA3) e o certificado A1, que entra mas nunca sai.
/// O aviso de vencimento (CA5) é lido pela moldura do painel.
/// </summary>
public static class VaultEndpoints
{
    public static void MapVaultEndpoints(this RouteGroupBuilder panel)
    {
        var vault = panel.MapGroup("/cofre").RequirePermission(Permission.VaultManage);

        // CA3: tipo, nome, últimos 4 caracteres e validade; nunca o valor.
        vault.MapGet("/", async (IVaultPanel service, CancellationToken ct) => TypedResults.Ok(await service.SecretsAsync(ct)));

        vault.MapGet("/certificado", async Task<Results<Ok<CertificateView>, NoContent>> (IVaultPanel service, CancellationToken ct) =>
            await service.CertificateAsync(ct) is { } certificate ? TypedResults.Ok(certificate) : TypedResults.NoContent());

        vault.MapPut("/certificado", async Task<Results<Ok<CertificateView>, ProblemHttpResult>> (CertificateRequest request, IVaultPanel service, CancellationToken ct) =>
        {
            byte[] pfx;
            try
            {
                pfx = Convert.FromBase64String(request.PfxBase64 ?? string.Empty);
            }
            catch (FormatException)
            {
                return AuthEndpoints.Problem(StatusCodes.Status400BadRequest, "Arquivo do certificado inválido.");
            }
            try
            {
                return TypedResults.Ok(await service.SaveCertificateAsync(pfx, request.Password ?? string.Empty, ct));
            }
            catch (ArgumentException e)
            {
                return AuthEndpoints.Problem(StatusCodes.Status400BadRequest, e.Message.Split(" (Parameter", 2)[0]);
            }
        });

        vault.MapDelete("/certificado", async (IVaultPanel service, CancellationToken ct) =>
        {
            await service.RemoveCertificateAsync(ct);
            return TypedResults.NoContent();
        });

        vault.MapGet("/alerta", async Task<Results<Ok<CertificateAlert>, NoContent>> (IVaultPanel service, CancellationToken ct) =>
            await service.AlertAsync(ct) is { } alert ? TypedResults.Ok(alert) : TypedResults.NoContent());
    }
}
