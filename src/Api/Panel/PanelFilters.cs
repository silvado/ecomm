using Ecommerce.Application.Common;
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
public sealed class PanelTenantFilter(IMembershipLookup memberships, TenantScope scope, Application.Privacy.PanelActor actor) : IEndpointFilter
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
        Correlation.TenantId = tenantId;
        http.Items[typeof(PanelUser)] = new PanelUser(userId, access);
        // Para o registro de acesso a dados pessoais (RNF04).
        actor.UserId = userId;
        actor.Email = http.User.FindFirst(Microsoft.IdentityModel.JsonWebTokens.JwtRegisteredClaimNames.Email)?.Value ?? string.Empty;
        actor.Ip = http.Connection.RemoteIpAddress?.ToString();
        return await next(context);
    }
}

/// <summary>Rota que continua valendo com o painel somente leitura (pagar a assinatura, direitos do titular — LGPD).</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class AllowWhenPanelReadOnlyAttribute : Attribute;

/// <summary>
/// RF38 CA2: com a loja em somente leitura, suspensa ou em encerramento, o painel só consulta. Gravações (tudo que não é
/// GET/HEAD) são recusadas, menos as marcadas com <see cref="AllowWhenPanelReadOnlyAttribute"/>. A situação vem do catálogo
/// (cache de até 60 s). Roda depois de <see cref="PanelTenantFilter"/>.
/// </summary>
public sealed class PanelReadOnlyFilter(Application.Tenancy.ITenantCatalog catalog) : IEndpointFilter
{
    public const string ProblemType = "https://plataforma/problemas/painel-somente-leitura";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)
            || http.GetEndpoint()?.Metadata.GetMetadata<AllowWhenPanelReadOnlyAttribute>() is not null)
            return await next(context);
        var store = await catalog.FindByIdAsync(http.GetPanelUser().Access.TenantId, http.RequestAborted);
        if (store is null || !Domain.Platform.Tenant.IsPanelReadOnly(store.Status)) return await next(context);
        return AuthEndpoints.Problem(StatusCodes.Status403Forbidden, store.Status == Domain.Platform.TenantStatus.ReadOnly
            ? "Painel somente leitura por fatura em atraso. Pague em Assinatura para voltar a alterar."
            : "Loja suspensa ou em encerramento: o painel está somente para consulta. Veja Assinatura ou fale com a equipe da plataforma.", ProblemType);
    }
}

public static class PanelEndpointExtensions
{
    /// <summary>Marca a rota como permitida com o painel somente leitura (RF38).</summary>
    public static TBuilder AllowWhenPanelReadOnly<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new AllowWhenPanelReadOnlyAttribute());

    public static PanelUser GetPanelUser(this HttpContext http) =>
        http.Items[typeof(PanelUser)] as PanelUser ?? throw new InvalidOperationException("Rota sem PanelTenantFilter.");

    /// <summary>Exige a permissão (nunca o papel) — RF07 CA1.</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, Permission permission) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
            context.HttpContext.GetPanelUser().Access.Permissions.Contains(permission)
                ? await next(context)
                : AuthEndpoints.Problem(StatusCodes.Status403Forbidden, "Seu perfil não permite esta ação."));
}
