using Ecommerce.Application.Platform;
using Ecommerce.Domain.Catalog;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecommerce.Infrastructure.Platform;

/// <summary>
/// Plano vigente de uma loja (RF37), lido do banco da plataforma a cada consulta (sem cache: troca de plano vale na hora).
/// O PlatformDbContext é pedido ao contêiner só na hora: entre as dependências de um handler, o Wolverine não saberia qual
/// DbContext é o da transação.
/// </summary>
public sealed class PlanDirectory(IServiceProvider services)
{
    public async Task<TenantPlan> ForTenantAsync(Guid tenantId, CancellationToken ct)
    {
        var platform = services.GetRequiredService<PlatformDbContext>();
        var code = await platform.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.PlanCode).SingleAsync(ct);
        var name = await platform.Plans.AsNoTracking().Where(p => p.Code == code).Select(p => p.Name).SingleAsync(ct);
        var limits = await platform.PlanLimits.AsNoTracking().Where(l => l.PlanCode == code).ToDictionaryAsync(l => l.Key, l => l.Value, ct);
        var features = await platform.PlanFeatures.AsNoTracking().Where(f => f.PlanCode == code).Select(f => f.Feature).ToListAsync(ct);
        return new TenantPlan(code, name, limits, features.ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>Mensagem padrão de limite atingido, com o nome do plano.</summary>
    public static string LimitMessage(TenantPlan plan, string what, int limit) =>
        $"O plano {plan.Name} permite até {Thousands(limit)} {what}. Para mais, fale conosco e mude de plano.";

    /// <summary>"3.000" sem depender da cultura instalada.</summary>
    private static string Thousands(int value) => value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture).Replace(',', '.');

    public static string FeatureMessage(TenantPlan plan, string what) => $"O plano {plan.Name} não inclui {what}. Para usar, fale conosco e mude de plano.";
}

/// <summary>Cartão "Seu plano": plano, uso de cada limite e recursos (só leitura).</summary>
public sealed class PlanUsageService(PlanDirectory plans, TenantDbContext db, PlatformDbContext platform, Application.Ai.IAiUsage ai) : IPlanUsage
{
    public async Task<PlanUsageView> CurrentAsync(CancellationToken ct = default)
    {
        var tenantId = db.RequireTenantId();
        var plan = await plans.ForTenantAsync(tenantId, ct);
        var users = await platform.TenantMemberships.CountAsync(m => m.TenantId == tenantId, ct);
        var activeParts = await db.Parts.CountAsync(p => p.Status == PartStatus.Active, ct);
        var aiUsed = (await ai.CurrentMonthAsync(ct)).GetValueOrDefault(Domain.Ai.AiFeature.PartFromPhotos);
        return new PlanUsageView(plan.Code, plan.Name,
        [
            new PlanUsage(PlanLimit.ActiveParts, activeParts, plan.Limit(PlanLimit.ActiveParts)),
            new PlanUsage(PlanLimit.Users, users, plan.Limit(PlanLimit.Users)),
            // RF36: cadastros por foto bem-sucedidos no mês.
            new PlanUsage(PlanLimit.AiListingsPerMonth, aiUsed, plan.Limit(PlanLimit.AiListingsPerMonth)),
        ], plan.Features.Order(StringComparer.Ordinal).ToList());
    }
}
