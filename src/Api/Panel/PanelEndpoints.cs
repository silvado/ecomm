using Ecommerce.Api.Auth;
using Ecommerce.Application.Identity;
using Ecommerce.Application.Storage;
using Ecommerce.Application.Store;
using Ecommerce.Application.Vault;
using Ecommerce.Domain.Identity;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ecommerce.Api.Panel;

public sealed record AddUserRequest(string Email, TenantRole Role, string? TemporaryPassword);
public sealed record ChangeRoleRequest(TenantRole Role);
public sealed record MeResponse(Guid UserId, TenantAccessResponse Tenant);

/// <summary>Painel do lojista: rotas com tenant vindo do token e conferido no banco (<see cref="PanelTenantFilter"/>).</summary>
public static class PanelEndpoints
{
    public static void MapPanelEndpoints(this IEndpointRouteBuilder app)
    {
        var panel = app.MapGroup("/api/painel").RequireAuthorization().AddEndpointFilter<PanelTenantFilter>();

        panel.MapGet("/eu", (HttpContext http) =>
        {
            var user = http.GetPanelUser();
            return TypedResults.Ok(new MeResponse(user.UserId, TenantAccessResponse.From(user.Access)));
        });

        panel.MapPartEndpoints();

        var users = panel.MapGroup("/usuarios").RequirePermission(Permission.UsersManage);

        users.MapGet("/", async (HttpContext http, IUserAdministration admin, CancellationToken ct) =>
            TypedResults.Ok(await admin.ListAsync(http.GetPanelUser().Access.TenantId, ct)));

        users.MapPost("/", async Task<Results<Created<StoreUser>, Ok<StoreUser>, ProblemHttpResult>> (
            AddUserRequest request, HttpContext http, IUserAdministration admin, CancellationToken ct) =>
        {
            var tenantId = http.GetPanelUser().Access.TenantId;
            var (outcome, user) = await admin.AddAsync(tenantId, request.Email ?? string.Empty, request.Role, request.TemporaryPassword ?? string.Empty, ct);
            return outcome switch
            {
                AddUserOutcome.Created => TypedResults.Created($"/api/painel/usuarios/{user!.UserId}", user),
                AddUserOutcome.LinkedExisting => TypedResults.Ok(user!),
                AddUserOutcome.AlreadyMember => AuthEndpoints.Problem(StatusCodes.Status409Conflict, "Este e-mail já tem acesso à loja."),
                AddUserOutcome.PlanLimitReached => AuthEndpoints.Problem(StatusCodes.Status409Conflict,
                    "Limite de usuários do plano atingido. Remova um usuário ou mude de plano."),
                AddUserOutcome.InvalidEmail => AuthEndpoints.Problem(StatusCodes.Status400BadRequest, "E-mail inválido."),
                AddUserOutcome.InvalidPassword => AuthEndpoints.Problem(StatusCodes.Status400BadRequest,
                    $"A senha provisória precisa ter de {PasswordPolicy.MinLength} a {PasswordPolicy.MaxLength} caracteres."),
                _ => throw new InvalidOperationException(outcome.ToString()),
            };
        });

        users.MapPut("/{userId:guid}", async (Guid userId, ChangeRoleRequest request, HttpContext http, IUserAdministration admin, CancellationToken ct) =>
            Respond(await admin.ChangeRoleAsync(http.GetPanelUser().Access.TenantId, userId, request.Role, ct)));

        users.MapDelete("/{userId:guid}", async (Guid userId, HttpContext http, IUserAdministration admin, CancellationToken ct) =>
            Respond(await admin.RemoveAsync(http.GetPanelUser().Access.TenantId, userId, ct)));

        users.MapPost("/{userId:guid}/desbloquear", async (Guid userId, HttpContext http, IUserAdministration admin, CancellationToken ct) =>
            Respond(await admin.UnlockAsync(http.GetPanelUser().Access.TenantId, userId, ct)));

        // RF01: dados, aparência e textos da loja.
        var store = panel.MapGroup("/loja").RequirePermission(Permission.StoreManage);

        store.MapGet("/", async (IStoreProfileService profiles, CancellationToken ct) => TypedResults.Ok(await profiles.GetAsync(ct)));

        store.MapPut("/", async Task<Results<Ok<StoreProfile>, ProblemHttpResult>> (
            UpdateStoreProfile request, IStoreProfileService profiles, CancellationToken ct) =>
        {
            var (profile, error) = await profiles.UpdateAsync(request, ct);
            return profile is not null ? TypedResults.Ok(profile) : AuthEndpoints.Problem(StatusCodes.Status400BadRequest, error!);
        });

        // RF01 CA2: corpo da requisição = bytes da imagem (sem multipart). O tipo é detectado pelo conteúdo.
        store.MapPut("/logo", async Task<Results<Ok<StoreProfile>, ProblemHttpResult>> (
            HttpRequest request, IStoreProfileService profiles, CancellationToken ct) =>
        {
            var (profile, error) = await profiles.ReplaceLogoAsync(request.Body, ct);
            return profile is not null ? TypedResults.Ok(profile) : AuthEndpoints.Problem(StatusCodes.Status400BadRequest, error!);
        });

        store.MapDelete("/logo", async (IStoreProfileService profiles, CancellationToken ct) => TypedResults.Ok(await profiles.RemoveLogoAsync(ct)));

        // Prévia no painel (a URL pública só responde no domínio da loja).
        store.MapGet("/logo", async Task<Results<FileStreamHttpResult, NotFound>> (
            HttpResponse response, IStoreProfileService profiles, CancellationToken ct) =>
        {
            if (await profiles.GetLogoAsync(null, ct) is not { } logo) return TypedResults.NotFound();
            response.Headers.CacheControl = "no-store";
            return LogoFile(response, logo);
        });

        // RF06 CA3: só metadados (tipo, últimos 4 caracteres, validade).
        panel.MapGet("/cofre", async (ISecretVault vault, CancellationToken ct) => TypedResults.Ok(await vault.ListAsync(ct)))
            .RequirePermission(Permission.VaultManage);
    }

    /// <summary>
    /// Serve uma imagem enviada por usuário: o navegador não pode reinterpretar o tipo (nosniff) nem executar nada
    /// mesmo que o arquivo seja aberto direto (CSP sem permissões).
    /// </summary>
    internal static FileStreamHttpResult LogoFile(HttpResponse response, StoredFile logo)
    {
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        return TypedResults.Stream(logo.Content, logo.ContentType);
    }

    private static Results<NoContent, ProblemHttpResult> Respond(UserChangeOutcome outcome) => outcome switch
    {
        UserChangeOutcome.Done => TypedResults.NoContent(),
        UserChangeOutcome.NotFound => AuthEndpoints.Problem(StatusCodes.Status404NotFound, "Usuário não encontrado nesta loja."),
        UserChangeOutcome.LastOwner => AuthEndpoints.Problem(StatusCodes.Status409Conflict, "A loja precisa de pelo menos um Dono."),
        _ => throw new InvalidOperationException(outcome.ToString()),
    };
}
