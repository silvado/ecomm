using System.Text;
using Ecommerce.Infrastructure.Vehicles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ecommerce.Migrator;

/// <summary>
/// Importa a tabela de veículos da plataforma (RF09 CA2) e replica para os bancos de lojas, até existir o painel de
/// superadmin (E4):
/// <code>docker compose run --rm -v ./deploy/veiculos:/dados migrator importar-veiculos /dados/planilha.csv</code>
/// Sem arquivo, usa a amostra embutida (só para desenvolvimento).
/// </summary>
internal static class ImportVehiclesCommand
{
    public const string Name = "importar-veiculos";
    public const string SampleFile = "veiculos-amostra.csv";

    public static async Task<int> RunAsync(IHost host, string[] args, TextWriter output)
    {
        var path = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, SampleFile);
        if (!File.Exists(path))
        {
            await output.WriteLineAsync($"Arquivo não encontrado: {path}");
            return 2;
        }

        var report = await ImportAsync(host, path);
        if (!report.Succeeded)
        {
            await output.WriteLineAsync("Nada foi importado. Corrija a planilha:");
            foreach (var error in report.Errors) await output.WriteLineAsync("  " + error);
            return 1;
        }

        await output.WriteLineAsync($"Importado e replicado: {report.Brands} marcas, {report.Models} modelos; versões novas {report.VersionsCreated}, " +
            $"atualizadas {report.VersionsUpdated}, descontinuadas {report.VersionsDiscontinued}.");
        return 0;
    }

    public static async Task<VehicleImportReport> ImportAsync(IHost host, string path)
    {
        await using var scope = host.Services.CreateAsyncScope();
        using var reader = new StreamReader(path, Encoding.UTF8);
        return await scope.ServiceProvider.GetRequiredService<VehicleImporter>().ImportAsync(reader);
    }
}
