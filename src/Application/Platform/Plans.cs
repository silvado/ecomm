namespace Ecommerce.Application.Platform;

/// <summary>Plano vigente da loja (RF37): limites (nulo = sem limite) e recursos habilitados.</summary>
public sealed record TenantPlan(string Code, string Name, IReadOnlyDictionary<string, int> Limits, IReadOnlySet<string> Features)
{
    public int? Limit(string key) => Limits.TryGetValue(key, out var value) ? value : null;

    public bool Has(string feature) => Features.Contains(feature);
}

/// <summary>Uso atual contra cada limite (cartão "Seu plano").</summary>
public sealed record PlanUsage(string Key, int Used, int? Limit);

public sealed record PlanUsageView(string Code, string Name, IReadOnlyList<PlanUsage> Usage, IReadOnlyList<string> Features);

/// <summary>Plano da loja do escopo e uso dos limites. Lido do banco a cada consulta: trocar o plano vale na hora (RF37 CA2).</summary>
public interface IPlanUsage
{
    Task<PlanUsageView> CurrentAsync(CancellationToken ct = default);
}
