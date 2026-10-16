using Ecommerce.Api.Auth;
using Ecommerce.Application.Billing;
using Ecommerce.Application.Platform;
using Ecommerce.Application.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ecommerce.Api.Platform;

public sealed record PlatformCreateTenantRequest(string Slug, string Cnpj, string RazaoSocial, string Nome, string Dono);
public sealed record PlatformCreatedTenantResponse(Guid Id, string Slug, string Host, string OwnerEmail, string? TemporaryPassword);
public sealed record PlatformPlanRequest(string Plano);
public sealed record PlatformCustomStorefrontRequest(bool Ligada);
public sealed record PlatformMeResponse(string Email);
public sealed record PlatformPriceRequest(decimal Valor);
public sealed record PlatformSetupFeeRequest(decimal Valor, DateOnly Vencimento);
public sealed record PlatformInvoiceNoteRequest(string? Observacao);

public sealed record PlatformThemeRequest(string? Css, string? Cabecalho, string? Rodape)
{
    public SavePlatformTheme ToCommand() => new(Css, Cabecalho, Rodape);
}

public sealed record PlatformPageRequest(string? Endereco, string? Titulo, string? Conteudo, bool Publicada, int Ordem)
{
    public SavePlatformPage ToCommand() => new(Endereco, Titulo, Conteudo, Publicada, Ordem);
}

/// <summary>Tema depois de salvar e o que a limpeza removeu (a tela mostra para a equipe).</summary>
public sealed record PlatformThemeSaved(PlatformTheme Theme, IReadOnlyList<string> Removed);

/// <summary>
/// Sessão de superadmin conferida no banco a cada requisição: sair ou trocar a senha derruba o token na hora.
/// Preenche quem está agindo, para a auditoria.
/// </summary>
public sealed class PlatformSessionFilter(IPlatformAdminAuth auth, PlatformActor actor) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.User.PlatformAdminId() is not { } adminId || http.User.PlatformSessionId() is not { } sessionId
            || !await auth.IsSessionActiveAsync(adminId, sessionId, http.RequestAborted))
            return AuthEndpoints.Problem(StatusCodes.Status401Unauthorized, "Sessão expirada. Entre novamente.");

        actor.AdminId = adminId;
        actor.Email = http.User.FindFirst(Microsoft.IdentityModel.JsonWebTokens.JwtRegisteredClaimNames.Email)?.Value ?? string.Empty;
        actor.Ip = http.Connection.RemoteIpAddress?.ToString();
        http.Response.Headers.CacheControl = "no-store";
        return await next(context);
    }
}

/// <summary>Painel da plataforma (RF07 CA3, ADR-0007): só com senha + MFA; só dados do banco plataforma; tudo auditado.</summary>
public static class PlatformEndpoints
{
    public static void MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        var platform = app.MapGroup("/api/plataforma").RequireAuthorization(PlatformTokens.Policy).AddEndpointFilter<PlatformSessionFilter>();

        platform.MapGet("/eu", (HttpContext http) =>
            TypedResults.Ok(new PlatformMeResponse(http.RequestServices.GetRequiredService<PlatformActor>().Email)));

        platform.MapGet("/lojas", async (string? busca, string? status, IPlatformConsole console, CancellationToken ct) =>
            TypedResults.Ok(await console.ListTenantsAsync(busca, status, ct)));

        platform.MapPost("/lojas", async Task<Results<Created<PlatformCreatedTenantResponse>, ProblemHttpResult>> (
            PlatformCreateTenantRequest request, IPlatformConsole console, CancellationToken ct) =>
        {
            var result = await console.CreateTenantAsync(new CreateTenantRequest(
                request.Slug ?? string.Empty, request.Cnpj ?? string.Empty, request.RazaoSocial ?? string.Empty,
                request.Nome ?? string.Empty, request.Dono ?? string.Empty), ct);
            if (result.Tenant is not { } created)
                return AuthEndpoints.Problem(StatusCodes.Status400BadRequest, result.Error switch
                {
                    CreateTenantError.InvalidSlug => "Endereço inválido: 3 a 40 letras minúsculas, números ou hífen, sem hífen nas pontas ou duplo, e não reservado.",
                    CreateTenantError.SlugTaken => "Já existe uma loja com este endereço.",
                    CreateTenantError.InvalidCnpj => "CNPJ inválido.",
                    CreateTenantError.CnpjTaken => "Já existe uma loja com este CNPJ.",
                    CreateTenantError.InvalidName => "Razão social e nome fantasia são obrigatórios (até 200 e 120 caracteres).",
                    CreateTenantError.InvalidOwnerEmail => "E-mail do Dono inválido.",
                    _ => "Não foi possível criar a loja.",
                });
            return TypedResults.Created($"/api/plataforma/lojas/{created.TenantId}",
                new PlatformCreatedTenantResponse(created.TenantId, created.Slug, created.Host, created.OwnerEmail, created.TemporaryPassword));
        });

        platform.MapGet("/lojas/{id:guid}", async Task<Results<Ok<PlatformTenantDetail>, NotFound>> (Guid id, IPlatformConsole console, CancellationToken ct) =>
            await console.GetTenantAsync(id, ct) is { } detail ? TypedResults.Ok(detail) : TypedResults.NotFound());

        platform.MapPut("/lojas/{id:guid}/plano", async (Guid id, PlatformPlanRequest request, IPlatformConsole console, CancellationToken ct) =>
            Respond(await console.ChangePlanAsync(id, request.Plano ?? string.Empty, ct)));

        platform.MapPost("/lojas/{id:guid}/suspender", async (Guid id, IPlatformConsole console, CancellationToken ct) =>
            Respond(await console.SuspendAsync(id, ct)));

        platform.MapPost("/lojas/{id:guid}/reativar", async (Guid id, IPlatformConsole console, CancellationToken ct) =>
            Respond(await console.ReactivateAsync(id, ct)));

        platform.MapPut("/lojas/{id:guid}/loja-personalizada", async (Guid id, PlatformCustomStorefrontRequest request, IPlatformConsole console, CancellationToken ct) =>
            Respond(await console.SetCustomStorefrontAsync(id, request.Ligada, ct)));

        // RF40 CA1: tema e páginas exclusivos (limpos ao salvar; a vitrine só aplica com a loja personalizada ligada).
        platform.MapGet("/lojas/{id:guid}/tema", async Task<Results<Ok<PlatformTheme>, NotFound>> (Guid id, IPlatformThemeService themes, CancellationToken ct) =>
            await themes.GetAsync(id, ct) is { } theme ? TypedResults.Ok(theme) : TypedResults.NotFound());

        platform.MapPost("/lojas/{id:guid}/tema/previa", (Guid id, PlatformThemeRequest request, IPlatformThemeService themes) =>
            TypedResults.Ok(themes.Preview(request.ToCommand())));

        platform.MapPut("/lojas/{id:guid}/tema", async (Guid id, PlatformThemeRequest request, IPlatformThemeService themes, CancellationToken ct) =>
            RespondTheme(await themes.SaveThemeAsync(id, request.ToCommand(), ct)));

        platform.MapPost("/lojas/{id:guid}/paginas", async (Guid id, PlatformPageRequest request, IPlatformThemeService themes, CancellationToken ct) =>
            RespondTheme(await themes.CreatePageAsync(id, request.ToCommand(), ct)));

        platform.MapPut("/lojas/{id:guid}/paginas/{pageId:guid}", async (Guid id, Guid pageId, PlatformPageRequest request, IPlatformThemeService themes, CancellationToken ct) =>
            RespondTheme(await themes.UpdatePageAsync(id, pageId, request.ToCommand(), ct)));

        platform.MapDelete("/lojas/{id:guid}/paginas/{pageId:guid}", async (Guid id, Guid pageId, IPlatformThemeService themes, CancellationToken ct) =>
            RespondTheme(await themes.DeletePageAsync(id, pageId, ct)));

        platform.MapGet("/planos",async (IPlatformConsole console, CancellationToken ct) => TypedResults.Ok(await console.PlansAsync(ct)));

        // RF38: mensalidade do plano (vale para as faturas geradas depois).
        platform.MapPut("/planos/{code}/preco", async Task<Results<Ok<IReadOnlyList<PlatformPlan>>, ProblemHttpResult>> (
            string code, PlatformPriceRequest request, ISubscriptionAdmin billing, IPlatformConsole console, CancellationToken ct) =>
            await billing.SetPlanPriceAsync(code, request.Valor, ct) is { } error
                ? AuthEndpoints.Problem(StatusCodes.Status400BadRequest, error)
                : TypedResults.Ok(await console.PlansAsync(ct)));

        // RF38: cobrança da loja — faturas, taxa de implantação, pagamento manual e cancelamento.
        var billing = platform.MapGroup("/lojas/{id:guid}/cobranca");

        billing.MapGet("/", async Task<Results<Ok<PlatformBillingView>, NotFound>> (Guid id, ISubscriptionAdmin service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } view ? TypedResults.Ok(view) : TypedResults.NotFound());

        billing.MapPost("/implantacao", async (Guid id, PlatformSetupFeeRequest request, ISubscriptionAdmin service, CancellationToken ct) =>
            RespondBilling(await service.AddSetupFeeAsync(id, request.Valor, request.Vencimento, ct)));

        billing.MapPost("/faturas/{invoiceId:guid}/pagamento-manual", async (Guid id, Guid invoiceId, PlatformInvoiceNoteRequest request,
            ISubscriptionAdmin service, CancellationToken ct) =>
            RespondBilling(await service.RecordManualPaymentAsync(id, invoiceId, request.Observacao, ct)));

        billing.MapPost("/faturas/{invoiceId:guid}/cancelar", async (Guid id, Guid invoiceId, PlatformInvoiceNoteRequest request,
            ISubscriptionAdmin service, CancellationToken ct) =>
            RespondBilling(await service.CancelInvoiceAsync(id, invoiceId, request.Observacao, ct)));

        platform.MapGet("/auditoria", async (Guid? loja, string? acao, string? email, int? pagina, IPlatformConsole console, CancellationToken ct) =>
            TypedResults.Ok(await console.AuditAsync(new PlatformAuditFilter(loja, acao, email, pagina ?? 1), ct)));
    }

    private static Results<Ok<PlatformBillingView>, ProblemHttpResult> RespondBilling(PlatformBillingResult result) =>
        result.Billing is { } billing
            ? TypedResults.Ok(billing)
            : AuthEndpoints.Problem(result.NotFound ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest, result.Error!);

    private static Results<Ok<PlatformThemeSaved>, ProblemHttpResult> RespondTheme(PlatformThemeResult result) =>
        result.Theme is { } theme
            ? TypedResults.Ok(new PlatformThemeSaved(theme, result.Removed))
            : AuthEndpoints.Problem(result.NotFound ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest, result.Error!);

    private static Results<Ok<PlatformTenantDetail>, ProblemHttpResult> Respond(PlatformTenantResult result) =>
        result.Tenant is { } tenant
            ? TypedResults.Ok(tenant)
            : AuthEndpoints.Problem(result.NotFound ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest, result.Error!);
}
