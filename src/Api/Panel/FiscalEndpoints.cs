using Ecommerce.Api.Auth;
using Ecommerce.Application.Catalog;
using Ecommerce.Application.Fiscal;
using Ecommerce.Domain.Fiscal;
using Ecommerce.Domain.Identity;
using Ecommerce.Infrastructure.Fiscal;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wolverine;

namespace Ecommerce.Api.Panel;

public sealed record EmitInvoiceRequest(FiscalRecipient? Recipient);
public sealed record ExternalInvoiceRequest(string? AccessKey, string? XmlBase64);
public sealed record CancelInvoiceRequest(string? Justification);

/// <summary>
/// NF-e (RF19): dados fiscais da loja (Dono), da peça (catálogo) e emissão, registro externo, cancelamento e arquivos no
/// pedido (Dono e Operador).
/// </summary>
public static class FiscalEndpoints
{
    public static void MapFiscalEndpoints(this RouteGroupBuilder panel)
    {
        panel.MapGet("/loja/fiscal", async (IFiscalService fiscal, CancellationToken ct) => TypedResults.Ok(await fiscal.GetSettingsAsync(ct)))
            .RequirePermission(Permission.StoreManage);

        panel.MapPut("/loja/fiscal", async Task<Results<Ok<FiscalSettingsView>, ProblemHttpResult>> (FiscalSettings request, IFiscalService fiscal, CancellationToken ct) =>
        {
            try
            {
                return TypedResults.Ok(await fiscal.SaveSettingsAsync(request, ct));
            }
            catch (ArgumentException e)
            {
                return Bad(e);
            }
        }).RequirePermission(Permission.StoreManage);

        panel.MapPut("/pecas/{id:guid}/fiscal", async Task<Results<Ok<PartView>, ProblemHttpResult>> (
            Guid id, PartFiscalData request, IFiscalService fiscal, IPartQueries queries, CancellationToken ct) =>
        {
            try
            {
                return await fiscal.SetPartFiscalAsync(id, request, ct)
                    ? TypedResults.Ok((await queries.GetAsync(id, ct))!)
                    : AuthEndpoints.Problem(StatusCodes.Status404NotFound, "Peça não encontrada.");
            }
            catch (ArgumentException e)
            {
                return Bad(e);
            }
        }).RequirePermission(Permission.CatalogManage);

        var invoices = panel.MapGroup("/pedidos/{id:guid}/nfe").RequirePermission(Permission.OrdersManage);

        invoices.MapGet("/", async Task<Results<Ok<InvoiceDraft>, ProblemHttpResult>> (Guid id, IFiscalService fiscal, CancellationToken ct) =>
            await fiscal.DraftAsync(id, ct) is { } draft ? TypedResults.Ok(draft) : OrderNotFound());

        invoices.MapPost("/", async Task<Results<Ok<InvoiceDraft>, ProblemHttpResult>> (
            Guid id, EmitInvoiceRequest request, HttpContext http, IMessageBus bus, IFiscalService fiscal, CancellationToken ct) =>
        {
            var user = http.GetPanelUser();
            FiscalCommandResult result;
            try
            {
                result = await bus.InvokeForTenantAsync<FiscalCommandResult>(user.Access.TenantId.ToString(), new EmitInvoice(id, user.UserId, request.Recipient), ct);
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                result = new FiscalCommandResult(FiscalCommandOutcome.NotAllowed, "Este pedido já tem NF-e autorizada ou em processamento.");
            }
            return await Respond(result, id, fiscal, ct);
        });

        invoices.MapPost("/externa", async Task<Results<Ok<InvoiceDraft>, ProblemHttpResult>> (
            Guid id, ExternalInvoiceRequest request, HttpContext http, IMessageBus bus, IFiscalService fiscal, CancellationToken ct) =>
        {
            byte[]? xml = null;
            if (!string.IsNullOrWhiteSpace(request.XmlBase64))
            {
                try
                {
                    xml = Convert.FromBase64String(request.XmlBase64);
                }
                catch (FormatException)
                {
                    return AuthEndpoints.Problem(StatusCodes.Status400BadRequest, "Arquivo XML inválido.");
                }
                if (xml.Length > FiscalHandler.MaxXmlBytes) return AuthEndpoints.Problem(StatusCodes.Status400BadRequest, "XML acima de 1 MB.");
            }
            var user = http.GetPanelUser();
            FiscalCommandResult result;
            try
            {
                result = await bus.InvokeForTenantAsync<FiscalCommandResult>(user.Access.TenantId.ToString(),
                    new RegisterExternalInvoice(id, user.UserId, request.AccessKey, xml), ct);
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                result = new FiscalCommandResult(FiscalCommandOutcome.NotAllowed, "Este pedido já tem NF-e registrada.");
            }
            return await Respond(result, id, fiscal, ct);
        });

        invoices.MapPost("/{documentId:guid}/cancelar", async Task<Results<Ok<InvoiceDraft>, ProblemHttpResult>> (
            Guid id, Guid documentId, CancelInvoiceRequest request, HttpContext http, IMessageBus bus, IFiscalService fiscal, CancellationToken ct) =>
        {
            var user = http.GetPanelUser();
            var draft = await fiscal.DraftAsync(id, ct);
            if (draft is null || draft.Documents.All(d => d.Id != documentId))
                return AuthEndpoints.Problem(StatusCodes.Status404NotFound, "NF-e não encontrada neste pedido.");
            var result = await bus.InvokeForTenantAsync<FiscalCommandResult>(user.Access.TenantId.ToString(),
                new CancelInvoice(documentId, user.UserId, request.Justification), ct);
            return await Respond(result, id, fiscal, ct);
        });

        // XML e DANFE só pelo painel (storage privado), como anexo.
        invoices.MapGet("/{documentId:guid}/{arquivo:regex(^(xml|danfe)$)}", async Task<Results<FileStreamHttpResult, ProblemHttpResult>> (
            Guid id, Guid documentId, string arquivo, IFiscalService fiscal, HttpResponse response, CancellationToken ct) =>
        {
            var draft = await fiscal.DraftAsync(id, ct);
            if (draft is null || draft.Documents.All(d => d.Id != documentId) || await fiscal.FileAsync(documentId, arquivo == "xml" ? FiscalFile.Xml : FiscalFile.Danfe, ct) is not { } found)
                return AuthEndpoints.Problem(StatusCodes.Status404NotFound, "Arquivo não encontrado.");
            response.Headers.CacheControl = "no-store";
            response.Headers.XContentTypeOptions = "nosniff";
            return TypedResults.Stream(found.File.Content, found.File.ContentType, found.FileName);
        });
    }

    private static async Task<Results<Ok<InvoiceDraft>, ProblemHttpResult>> Respond(FiscalCommandResult result, Guid orderId, IFiscalService fiscal, CancellationToken ct) =>
        result.Outcome switch
        {
            FiscalCommandOutcome.Done => TypedResults.Ok((await fiscal.DraftAsync(orderId, ct))!),
            FiscalCommandOutcome.NotFound => AuthEndpoints.Problem(StatusCodes.Status404NotFound, result.Message ?? "Não encontrado."),
            FiscalCommandOutcome.NotAllowed => AuthEndpoints.Problem(StatusCodes.Status409Conflict, result.Message!),
            FiscalCommandOutcome.Refused => AuthEndpoints.Problem(StatusCodes.Status422UnprocessableEntity, result.Message!),
            _ => AuthEndpoints.Problem(StatusCodes.Status400BadRequest, result.Message ?? "Dados inválidos."),
        };

    private static ProblemHttpResult OrderNotFound() => AuthEndpoints.Problem(StatusCodes.Status404NotFound, "Pedido não encontrado.");

    private static ProblemHttpResult Bad(ArgumentException e) => AuthEndpoints.Problem(StatusCodes.Status400BadRequest, e.Message.Split(" (Parameter", 2)[0]);
}
