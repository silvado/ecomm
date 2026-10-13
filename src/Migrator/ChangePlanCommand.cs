using Ecommerce.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ecommerce.Migrator;

/// <summary>
/// Troca o plano de uma loja (RF37) até existir a cobrança (RF38) e o painel de superadmin (E4):
/// <code>docker compose run --rm migrator plano --slug pecas-do-joao --plano profissional</code>
/// Vale na hora. Ao baixar de plano, o que já passou do novo limite continua; só não dá para acrescentar mais.
/// </summary>
internal static class ChangePlanCommand
{
    public const string Name = "plano";

    public static async Task<int> RunAsync(IHost host, string[] args, TextWriter output)
    {
        var slug = Option(args, "--slug");
        var planCode = Option(args, "--plano")?.ToLowerInvariant();
        if (slug is null || planCode is null)
        {
            await output.WriteLineAsync("Uso: plano --slug <slug> --plano essencial|profissional|completo");
            return 2;
        }

        await using var scope = host.Services.CreateAsyncScope();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var plan = await platform.Plans.AsNoTracking().SingleOrDefaultAsync(p => p.Code == planCode);
        if (plan is null)
        {
            await output.WriteLineAsync($"Plano \"{planCode}\" não existe. Planos: {string.Join(", ", await platform.Plans.Select(p => p.Code).ToListAsync())}.");
            return 1;
        }
        var normalizedSlug = slug.ToLowerInvariant();
        var tenant = await platform.Tenants.SingleOrDefaultAsync(t => t.Slug == normalizedSlug);
        if (tenant is null)
        {
            await output.WriteLineAsync($"Loja \"{slug}\" não encontrada.");
            return 1;
        }

        var previous = tenant.PlanCode;
        tenant.ChangePlan(plan.Code);
        await platform.SaveChangesAsync();
        await output.WriteLineAsync($"Loja {tenant.Slug}: plano {previous} → {plan.Code} ({plan.Name}). Vale a partir de agora.");
        return 0;
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length && !string.IsNullOrWhiteSpace(args[index + 1]) ? args[index + 1].Trim() : null;
    }
}
