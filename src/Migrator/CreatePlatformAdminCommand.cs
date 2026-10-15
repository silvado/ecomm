using Ecommerce.Infrastructure.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ecommerce.Migrator;

/// <summary>
/// Cria um superadmin da plataforma (ADR-0007), só pelo servidor:
/// <code>docker compose run --rm migrator criar-superadmin --email voce@empresa.com.br</code>
/// A senha provisória aparece só na saída deste comando; no primeiro acesso, troca de senha e cadastro do MFA são obrigatórios.
/// </summary>
internal static class CreatePlatformAdminCommand
{
    public const string Name = "criar-superadmin";

    public static async Task<int> RunAsync(IHost host, string[] args, TextWriter output)
    {
        var index = Array.IndexOf(args, "--email");
        if (index < 0 || index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            await output.WriteLineAsync("Uso: criar-superadmin --email <e-mail>");
            return 2;
        }

        await using var scope = host.Services.CreateAsyncScope();
        var provisioning = ActivatorUtilities.CreateInstance<PlatformAdminProvisioning>(scope.ServiceProvider);
        if (await provisioning.CreateAsync(args[index + 1]) is not { } created)
        {
            await output.WriteLineAsync("E-mail inválido ou já cadastrado como superadmin.");
            return 1;
        }

        await output.WriteLineAsync($"Superadmin criado: {created.Admin.Email}");
        await output.WriteLineAsync($"Senha provisória (mostrada só agora; no primeiro acesso troque a senha e cadastre o app autenticador): {created.TemporaryPassword}");
        return 0;
    }
}
