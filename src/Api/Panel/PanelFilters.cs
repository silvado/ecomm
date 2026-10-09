using Ecommerce.Api.Auth;
using Ecommerce.Application.Identity;
using Ecommerce.Domain.Identity;
using Ecommerce.Infrastructure.Tenancy;

namespace Ecommerce.Api.Panel;

/// <summary>Usuário do painel já conferido contra o banco nesta requisição.</summary>
public sealed record PanelUser(Guid UserId, TenantAccess Access);

public static class PanelProblemTypes
{
    public const string ChooseStore = "https://plataforma/problemas/escolher-loja";
    public const string PasswordChangeRequired = "https://plataforma/problemas/troca-de-senha-obrigatoria";
}

/// <summary>
/// Rotas do painel: o token diz a loja; o vínculo no banco (cache curto) diz se o usuário ainda pode e com qual papel.
/// Só então o tenant do escopo é fixado (RLS) — usuário removido ou de outra loja nunca chega ao banco de lojas.
/// </summary>
public sealed class PanelTenantFilter(IMembershipLookup memberships, TenantScope scope) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.User.PanelUserId() is not { } userId)
            return AuthEndpoints.Problem(StatusCodes.Status401Unauthorized, "Sessão inválida.");
        if (http.User.MustChangePassword())
            return AuthEndpoints.Problem(StatusCodes.Status403Forbidden, "Troque a senha provisória para continuar.", PanelProblemTypes.PasswordChangeRequired);
        if (http.User.PanelTenantId() is not { } tenantId)
            return AuthEndpoints.Problem(StatusCodes.Status403Forbidden, "Escolha uma loja.", PanelProblemTypes.ChooseStore);

        var access = await memberships.FindAsync(userId, tenantId, http.RequestAborted);
        if (access is null)
            return AuthEndpoints.Problem(StatusCodes.Status403Forbidden, "Você não tem acesso a esta loja.");

        scope.Set(tenantId);
        http.Items[typeof(PanelUser)] = new PanelUser(userId, access);
        return await next(context);
    }
}

public static class PanelEndpointExtensions
{
    public static PanelUser GetPanelUser(this HttpContext http) =>
        http.Items[typeof(PanelUser)] as PanelUser ?? throw new InvalidOperationException("Rota sem PanelTenantFilter.");

    /// <summary>Exige a permissão (nunca o papel) — RF07 CA1.</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, Permission permission) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
            context.HttpContext.GetPanelUser().Access.Permissions.Contains(permission)
                ? await next(context)
                : AuthEndpoints.Problem(StatusCodes.Status403Forbidden, "Seu perfil não permite esta ação."));
}
