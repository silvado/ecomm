using Ecommerce.Application.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ecommerce.Migrator;

/// <summary>
/// Cria uma loja pela linha de comando, no servidor (RF01/RF02), até existir o painel de superadmin (E4):
/// <code>docker compose run --rm migrator criar-loja --slug pecas-do-joao --cnpj 11.222.333/0001-81
///   --razao-social "Peças do João Ltda" --nome "Peças do João" --dono joao@pecas.com.br</code>
/// A senha provisória do Dono aparece só na saída deste comando (nunca em log).
/// </summary>
internal static class CreateStoreCommand
{
    public const string Name = "criar-loja";

    private static readonly string[] Required = ["--slug", "--cnpj", "--razao-social", "--nome", "--dono"];

    public static async Task<int> RunAsync(IHost host, string[] args, TextWriter output)
    {
        var options = Parse(args);
        var missing = Required.Where(r => !options.ContainsKey(r)).ToList();
        if (missing.Count > 0)
        {
            await output.WriteLineAsync($"Faltam opções: {string.Join(", ", missing)}.");
            await output.WriteLineAsync("Uso: criar-loja --slug <slug> --cnpj <cnpj> --razao-social <texto> --nome <nome fantasia> --dono <e-mail>");
            return 2;
        }

        await using var scope = host.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ITenantProvisioning>().CreateAsync(new CreateTenantRequest(
            options["--slug"], options["--cnpj"], options["--razao-social"], options["--nome"], options["--dono"]));

        if (result.Tenant is not { } created)
        {
            await output.WriteLineAsync(result.Error switch
            {
                CreateTenantError.InvalidSlug => "Slug inválido: 3 a 40 caracteres [a-z0-9-], sem hífen nas pontas ou duplo, e não reservado.",
                CreateTenantError.SlugTaken => "Já existe uma loja com este slug.",
                CreateTenantError.InvalidCnpj => "CNPJ inválido.",
                CreateTenantError.CnpjTaken => "Já existe uma loja com este CNPJ.",
                CreateTenantError.InvalidName => "Razão social e nome fantasia são obrigatórios (até 200 e 120 caracteres).",
                CreateTenantError.InvalidOwnerEmail => "E-mail do Dono inválido.",
                _ => $"Não foi possível criar a loja ({result.Error}).",
            });
            return 1;
        }

        await output.WriteLineAsync($"Loja criada: https://{created.Host}");
        await output.WriteLineAsync($"Dono: {created.OwnerEmail}");
        await output.WriteLineAsync(created.TemporaryPassword is { } password
            ? $"Senha provisória (mostrada só agora; troca obrigatória no primeiro acesso): {password}"
            : "O Dono já tinha conta na plataforma: entra com a senha que já usa.");
        return 0;
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < args.Length; i += 2)
            options[args[i]] = args[i + 1];
        return options;
    }
}
