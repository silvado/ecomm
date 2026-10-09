using Ecommerce.Application.Vehicles;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Vehicles;

/// <summary>Lê a réplica <c>ref</c> no banco de lojas (dados da plataforma, iguais para todas as lojas).</summary>
public sealed class VehicleCatalog(TenantDbContext db) : IVehicleCatalog
{
    public async Task<IReadOnlyList<VehicleOption>> BrandsAsync(CancellationToken ct = default) =>
        await db.VehicleBrands.AsNoTracking().OrderBy(b => b.Name).Select(b => new VehicleOption(b.Id, b.Name)).ToListAsync(ct);

    public async Task<IReadOnlyList<VehicleOption>> ModelsAsync(Guid brandId, CancellationToken ct = default) =>
        await db.VehicleModels.AsNoTracking().Where(m => m.BrandId == brandId).OrderBy(m => m.Name)
            .Select(m => new VehicleOption(m.Id, m.Name)).ToListAsync(ct);

    public async Task<IReadOnlyList<VehicleVersionOption>> VersionsAsync(Guid modelId, bool includeDiscontinued = false, CancellationToken ct = default) =>
        await db.VehicleVersions.AsNoTracking().Where(v => v.ModelId == modelId && (includeDiscontinued || !v.Discontinued))
            .OrderBy(v => v.YearFrom).ThenBy(v => v.Engine)
            .Select(v => new VehicleVersionOption(v.Id, v.Engine, v.YearFrom, v.YearTo, v.Discontinued)).ToListAsync(ct);
}
