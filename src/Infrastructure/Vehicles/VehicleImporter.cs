using System.Globalization;
using Ecommerce.Domain.Vehicles;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Vehicles;

public sealed record VehicleImportReport(
    IReadOnlyList<string> Errors, int Brands, int Models, int VersionsCreated, int VersionsUpdated, int VersionsDiscontinued)
{
    public bool Succeeded => Errors.Count == 0;
}

/// <summary>
/// Importa a tabela de veículos da plataforma (RF09 CA2) de uma planilha CSV e replica para o schema <c>ref</c> do
/// banco de lojas. Formato (UTF-8, separador ";", cabeçalho obrigatório):
/// <code>marca;modelo;motorizacao;ano_inicial;ano_final</code>
/// <c>ano_final</c> vazio = ainda em produção. Linhas começando com "#" são comentários.
/// Tudo ou nada: com qualquer erro, nada é gravado e o relatório lista as linhas. Idempotente; nunca apaga: versão que
/// saiu da planilha vira descontinuada (compatibilidades existentes continuam valendo).
/// Roda com o papel de migração (o app_user só lê a réplica).
/// </summary>
public sealed class VehicleImporter(PlatformDbContext platform, TenantDbContext tenants, TimeProvider clock)
{
    public const string Header = "marca;modelo;motorizacao;ano_inicial;ano_final";

    private sealed record Row(int Line, string Brand, string Model, string Engine, int YearFrom, int? YearTo);

    public async Task<VehicleImportReport> ImportAsync(TextReader csv, CancellationToken ct = default)
    {
        var currentYear = clock.GetUtcNow().Year;
        var (rows, errors) = await ParseAsync(csv, currentYear);
        if (errors.Count > 0) return new VehicleImportReport(errors, 0, 0, 0, 0, 0);

        await using var tx = await platform.Database.BeginTransactionAsync(ct);
        var brands = await platform.VehicleBrands.ToListAsync(ct);
        var models = await platform.VehicleModels.ToListAsync(ct);
        var versions = await platform.VehicleVersions.ToListAsync(ct);
        var (created, updated) = (0, 0);
        var seen = new HashSet<Guid>();

        foreach (var row in rows)
        {
            var brand = brands.SingleOrDefault(b => Same(b.Name, row.Brand));
            if (brand is null) { brand = new VehicleBrand(Guid.CreateVersion7(), row.Brand); brands.Add(brand); platform.VehicleBrands.Add(brand); }

            var model = models.SingleOrDefault(m => m.BrandId == brand.Id && Same(m.Name, row.Model));
            if (model is null) { model = new VehicleModel(Guid.CreateVersion7(), brand.Id, row.Model); models.Add(model); platform.VehicleModels.Add(model); }

            var version = versions.SingleOrDefault(v => v.ModelId == model.Id && Same(v.Engine, row.Engine) && v.YearFrom == row.YearFrom);
            if (version is null)
            {
                version = new VehicleVersion(Guid.CreateVersion7(), model.Id, row.Engine, row.YearFrom, row.YearTo, currentYear);
                versions.Add(version);
                platform.VehicleVersions.Add(version);
                created++;
            }
            else if (version.YearTo != row.YearTo || version.Discontinued)
            {
                version.ChangeYearTo(row.YearTo, currentYear);
                version.Restore();
                updated++;
            }
            seen.Add(version.Id);
        }

        var discontinued = 0;
        foreach (var version in versions.Where(v => !seen.Contains(v.Id) && !v.Discontinued))
        {
            version.Discontinue();
            discontinued++;
        }

        await platform.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await ReplicateAsync(ct);
        return new VehicleImportReport([], brands.Count, models.Count, created, updated, discontinued);
    }

    /// <summary>
    /// Copia a tabela inteira da plataforma para <c>ref.*</c> do banco de lojas (mesmos ids), numa transação.
    /// Idempotente: serve também para preparar um banco de lojas novo (RNF08).
    /// </summary>
    public async Task ReplicateAsync(CancellationToken ct = default)
    {
        var brands = await platform.VehicleBrands.AsNoTracking().ToListAsync(ct);
        var models = await platform.VehicleModels.AsNoTracking().ToListAsync(ct);
        var versions = await platform.VehicleVersions.AsNoTracking().ToListAsync(ct);

        await using var tx = await tenants.Database.BeginTransactionAsync(ct);
        foreach (var b in brands)
            await tenants.Database.ExecuteSqlAsync($"""
                INSERT INTO ref.vehicle_brands (id, name) VALUES ({b.Id}, {b.Name})
                ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name
                """, ct);
        foreach (var m in models)
            await tenants.Database.ExecuteSqlAsync($"""
                INSERT INTO ref.vehicle_models (id, brand_id, name) VALUES ({m.Id}, {m.BrandId}, {m.Name})
                ON CONFLICT (id) DO UPDATE SET brand_id = EXCLUDED.brand_id, name = EXCLUDED.name
                """, ct);
        foreach (var v in versions)
            await tenants.Database.ExecuteSqlAsync($"""
                INSERT INTO ref.vehicle_versions (id, model_id, engine, year_from, year_to, ml_reference, discontinued)
                VALUES ({v.Id}, {v.ModelId}, {v.Engine}, {v.YearFrom}, {v.YearTo}, {v.MlReference}, {v.Discontinued})
                ON CONFLICT (id) DO UPDATE SET model_id = EXCLUDED.model_id, engine = EXCLUDED.engine, year_from = EXCLUDED.year_from,
                    year_to = EXCLUDED.year_to, ml_reference = EXCLUDED.ml_reference, discontinued = EXCLUDED.discontinued
                """, ct);
        await tx.CommitAsync(ct);
    }

    private static async Task<(List<Row> Rows, List<string> Errors)> ParseAsync(TextReader csv, int currentYear)
    {
        var rows = new List<Row>();
        var errors = new List<string>();
        var header = (await csv.ReadLineAsync())?.Trim().TrimStart('﻿');
        if (!string.Equals(header, Header, StringComparison.OrdinalIgnoreCase))
            return (rows, [$"Linha 1: cabeçalho precisa ser \"{Header}\"."]);

        var lineNumber = 1;
        string? line;
        while ((line = await csv.ReadLineAsync()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            var cells = line.Split(';');
            if (cells.Length != 5) { errors.Add($"Linha {lineNumber}: esperadas 5 colunas, encontradas {cells.Length}."); continue; }
            if (!int.TryParse(cells[3].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var yearFrom))
            { errors.Add($"Linha {lineNumber}: ano inicial inválido."); continue; }
            int? yearTo = null;
            if (!string.IsNullOrWhiteSpace(cells[4]))
            {
                if (!int.TryParse(cells[4].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var end))
                { errors.Add($"Linha {lineNumber}: ano final inválido."); continue; }
                yearTo = end;
            }
            try
            {
                // Valida com as regras do domínio (nomes e anos) antes de qualquer gravação.
                var brandName = new VehicleBrand(Guid.Empty, cells[0]).Name;
                var modelName = new VehicleModel(Guid.Empty, Guid.Empty, cells[1]).Name;
                var engine = new VehicleVersion(Guid.Empty, Guid.Empty, cells[2], yearFrom, yearTo, currentYear).Engine;
                var row = new Row(lineNumber, brandName, modelName, engine, yearFrom, yearTo);
                if (rows.Any(r => Same(r.Brand, row.Brand) && Same(r.Model, row.Model) && Same(r.Engine, row.Engine) && r.YearFrom == row.YearFrom))
                { errors.Add($"Linha {lineNumber}: versão repetida na planilha."); continue; }
                rows.Add(row);
            }
            catch (ArgumentException e)
            {
                errors.Add($"Linha {lineNumber}: {e.Message.Split(" (Parameter", 2)[0]}");
            }
        }
        if (errors.Count == 0 && rows.Count == 0) errors.Add("A planilha não tem nenhuma versão.");
        return (rows, errors);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
