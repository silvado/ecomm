using Ecommerce.Api.Auth;
using Ecommerce.Application.Reports;
using Ecommerce.Domain.Identity;

namespace Ecommerce.Api.Panel;

public sealed record SyncGuardianRequest(bool Ativo);

/// <summary>
/// Guardião da sincronização (RF33) em Integrações: os problemas de agora, com explicação e ação sugerida (Dono e
/// Operador), e o aviso por e-mail aos Donos (só o Dono liga ou desliga).
/// </summary>
public static class SyncGuardianEndpoints
{
    public static void MapSyncGuardianEndpoints(this RouteGroupBuilder panel)
    {
        panel.MapGet("/integracoes/diagnostico", async (ISyncGuardian guardian, CancellationToken ct) => TypedResults.Ok(await guardian.CurrentAsync(ct)))
            .RequirePermission(Permission.OrdersManage);

        var settings = panel.MapGroup("/guardiao").RequirePermission(Permission.StoreManage);
        settings.MapGet("/", async (ISyncGuardian guardian, CancellationToken ct) => TypedResults.Ok(await guardian.SettingsAsync(ct)));
        settings.MapPut("/", async (SyncGuardianRequest request, ISyncGuardian guardian, CancellationToken ct) =>
            TypedResults.Ok(await guardian.SaveAsync(request.Ativo, ct)));
    }
}
