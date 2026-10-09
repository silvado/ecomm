using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Platform;
using Ecommerce.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ecommerce.Integration.Tests.Vehicles;

public sealed class VehicleImportFixture : IAsyncLifetime
{
    public PostgresEnvironment Env { get; private set; } = null!;

    public async Task InitializeAsync() => Env = await PostgresEnvironment.StartAsync();

    public async Task DisposeAsync() => await Env.DisposeAsync();
}

/// <summary>
/// RF09 CA2 — tabela de veículos da plataforma importada por planilha e replicada para ref.* (mesmos ids).
/// Os testes desta classe rodam em sequência no mesmo banco: cada um importa a planilha inteira que lhe interessa.
/// </summary>
public sealed class VehicleImportTests(VehicleImportFixture fx) : IClassFixture<VehicleImportFixture>
{
    private const string Header = "marca;modelo;motorizacao;ano_inicial;ano_final\n";

    private async Task<T> PlatformAsync<T>(Func<PlatformDbContext, Task<T>> query)
    {
        using var host = fx.Env.MigratorHost();
        await using var scope = host.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<PlatformDbContext>());
    }

    private async Task<List<(Guid Id, string Engine, int? YearTo, bool Discontinued)>> RefVersionsAsync(string model)
    {
        await using var connection = new NpgsqlConnection(fx.Env.TenantsConnectionString); // app_user: só leitura
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT v.id, v.engine, v.year_to, v.discontinued FROM ref.vehicle_versions v
            JOIN ref.vehicle_models m ON m.id = v.model_id WHERE m.name = @model ORDER BY v.engine
            """, connection);
        command.Parameters.AddWithValue("model", model);
        var rows = new List<(Guid, string, int?, bool)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt32(2), reader.GetBoolean(3)));
        return rows;
    }

    [Fact]
    public async Task Importa_e_replica_com_os_mesmos_ids_e_reimportar_nao_duplica()
    {
        var csv = Header + "Volkswagen;Gol;1.0 8V Flex;2009;2012\nVolkswagen;Gol;1.6 8V Flex;2009;2012\n# comentário\n\nFiat;Uno;1.0 Fire Flex;2004;2013\n";

        var first = await fx.Env.ImportVehiclesAsync(csv);
        var second = await fx.Env.ImportVehiclesAsync(csv.Replace("volkswagen", "Volkswagen", StringComparison.Ordinal).Replace("Gol;1.0", "GOL;1.0", StringComparison.Ordinal));

        Assert.True(first.Succeeded);
        Assert.Equal(3, first.VersionsCreated);
        Assert.Equal((0, 0), (second.VersionsCreated, second.VersionsUpdated)); // maiúsculas não criam outra versão
        var platformIds = await PlatformAsync(db => db.VehicleVersions.Join(db.VehicleModels, v => v.ModelId, m => m.Id, (v, m) => new { v.Id, m.Name })
            .Where(x => x.Name == "Gol").Select(x => x.Id).OrderBy(x => x).ToListAsync());
        Assert.Equal(platformIds, (await RefVersionsAsync("Gol")).Select(v => v.Id).Order());
    }

    [Fact]
    public async Task Versao_que_sai_da_planilha_e_descontinuada_e_volta_se_reaparecer()
    {
        var full = Header + "Ford;Ka Teste;1.0 Rocam Flex;2008;2013\nFord;Ka Teste;1.0 Ti-VCT Flex;2015;\n";
        await fx.Env.ImportVehiclesAsync(full);

        var report = await fx.Env.ImportVehiclesAsync(Header + "Ford;Ka Teste;1.0 Ti-VCT Flex;2015;2021\n");

        Assert.Equal((1, 1), (report.VersionsDiscontinued, report.VersionsUpdated));
        var refVersions = await RefVersionsAsync("Ka Teste");
        Assert.Equal([("1.0 Rocam Flex", (int?)2013, true), ("1.0 Ti-VCT Flex", 2021, false)], refVersions.Select(v => (v.Engine, v.YearTo, v.Discontinued)));

        await fx.Env.ImportVehiclesAsync(full);
        Assert.All(await RefVersionsAsync("Ka Teste"), v => Assert.False(v.Discontinued));
    }

    [Fact]
    public async Task Planilha_com_erro_nao_grava_nada_e_aponta_as_linhas()
    {
        var before = await PlatformAsync(db => db.VehicleVersions.CountAsync());
        var csv = Header +
            "Honda;Civic;1.8 Flex;2007;2011\n" +
            "Honda;Fit;1.4 Flex;dois mil;2014\n" +
            "Honda;City;1.5 Flex;2015;2010\n" +
            "Honda;;1.5 Flex;2015;2020\n" +
            "Honda;Civic;1.8 flex;2007;2011\n" +
            "Honda;HR-V;1.8\n";

        var report = await fx.Env.ImportVehiclesAsync(csv);

        Assert.False(report.Succeeded);
        Assert.Equal(5, report.Errors.Count);
        Assert.StartsWith("Linha 3:", report.Errors[0], StringComparison.Ordinal);
        Assert.Contains(report.Errors, e => e.StartsWith("Linha 6: versão repetida", StringComparison.Ordinal));
        Assert.Equal(before, await PlatformAsync(db => db.VehicleVersions.CountAsync()));
    }

    [Fact]
    public async Task Cabecalho_errado_e_recusado() =>
        Assert.StartsWith("Linha 1: cabeçalho", (await fx.Env.ImportVehiclesAsync("marca,modelo\nX,Y\n")).Errors.Single(), StringComparison.Ordinal);

    [Fact]
    public async Task Aplicacao_le_a_replica_mas_nao_escreve_nela()
    {
        await fx.Env.ImportVehiclesAsync(Header + "Toyota;Corolla;2.0 Flex;2011;2014\n");
        await using var connection = new NpgsqlConnection(fx.Env.TenantsConnectionString);
        await connection.OpenAsync();

        await using var read = new NpgsqlCommand("SELECT count(*) FROM ref.vehicle_brands", connection);
        Assert.True((long)(await read.ExecuteScalarAsync())! > 0);

        foreach (var sql in new[]
        {
            "INSERT INTO ref.vehicle_brands (id, name) VALUES (gen_random_uuid(), 'Intrusa')",
            "UPDATE ref.vehicle_versions SET discontinued = true",
            "DELETE FROM ref.vehicle_models",
        })
        {
            await using var write = new NpgsqlCommand(sql, connection);
            var error = await Assert.ThrowsAsync<PostgresException>(() => write.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
    }

    [Fact]
    public async Task Amostra_do_repositorio_e_valida()
    {
        var sample = Path.Combine(AppContext.BaseDirectory, "veiculos-amostra.csv");
        var report = await fx.Env.ImportVehiclesAsync(await File.ReadAllTextAsync(sample));

        Assert.True(report.Succeeded, string.Join("\n", report.Errors));
    }
}
