using Ecommerce.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ecommerce.Migrator;

/// <summary>
/// Liga ou desliga a loja personalizada (RF40, contratada por orçamento) até existir o painel de superadmin (E4):
/// <code>docker compose run --rm migrator loja-personalizada --slug pecas-do-joao --ligar</code>
/// Ligada, o Dono não muda mais logo, nome, cores nem textos pelo painel; desligada, volta a mudar.
/// </summary>
internal static class CustomStorefrontCommand
{
    public const string Name = "loja-personalizada";

    public static async Task<int> RunAsync(IHost host, string[] args, TextWriter output)
    {
        var slugIndex = Array.IndexOf(args, "--slug");
        var enable = args.Contains("--ligar");
        var disable = args.Contains("--desligar");
        if (slugIndex < 0 || slugIndex + 1 >= args.Length || enable == disable)
        {
            await output.WriteLineAsync("Uso: loja-personalizada --slug <slug> --ligar | --desligar");
            return 2;
        }

        await using var scope = host.Services.CreateAsyncScope();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var slug = args[slugIndex + 1].Trim().ToLowerInvariant();
        var tenant = await platform.Tenants.SingleOrDefaultAsync(t => t.Slug == slug);
        if (tenant is null)
        {
            await output.WriteLineAsync($"Loja \"{slug}\" não encontrada.");
            return 1;
        }

        tenant.SetCustomStorefront(enable, scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow());
        await platform.SaveChangesAsync();
        await output.WriteLineAsync(enable
            ? $"Loja personalizada ligada para {slug} desde {tenant.CustomStorefrontSince:dd/MM/yyyy HH:mm} (UTC). A aparência não muda mais pelo painel."
            : $"Loja personalizada desligada para {slug}. O Dono volta a editar a aparência.");
        return 0;
    }
}
