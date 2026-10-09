using Ecommerce.Application.Catalog;
using Ecommerce.Domain.Catalog;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ecommerce.Infrastructure.Catalog;

/// <summary>Compatibilidades da peça (RF09). A versão vem da réplica <c>ref</c>; a peça, do tenant do escopo (RLS).</summary>
public sealed class PartCompatibilityService(TenantDbContext db, TimeProvider clock) : IPartCompatibilities
{
    public async Task<CompatibilityResult> AddAsync(Guid partId, Guid vehicleVersionId, int? yearFrom, int? yearTo, CancellationToken ct = default)
    {
        var part = await db.Parts.Include(p => p.Compatibilities).SingleOrDefaultAsync(p => p.Id == partId, ct);
        if (part is null) return new CompatibilityResult(CompatibilityOutcome.NotFound, null, "Peça não encontrada.");
        var version = await db.VehicleVersions.AsNoTracking().Where(v => v.Id == vehicleVersionId)
            .Select(v => new VehicleVersionRange(v.Id, v.YearFrom, v.YearTo, v.Discontinued)).SingleOrDefaultAsync(ct);
        if (version is null) return new CompatibilityResult(CompatibilityOutcome.NotFound, null, "Versão de veículo não encontrada.");

        PartCompatibility compatibility;
        try { compatibility = part.AddCompatibility(version, yearFrom, yearTo, CompatibilitySource.Manual, clock.GetUtcNow()); }
        catch (ArgumentException e) { return new CompatibilityResult(CompatibilityOutcome.Invalid, null, e.Message.Split(" (Parameter", 2)[0]); }
        catch (InvalidOperationException e) { return new CompatibilityResult(CompatibilityOutcome.Conflict, null, e.Message); }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Mesmo cadastro em duas abas ao mesmo tempo: o índice único decide.
            return new CompatibilityResult(CompatibilityOutcome.Conflict, null, "Esta compatibilidade já está cadastrada.");
        }
        return new CompatibilityResult(CompatibilityOutcome.Done, compatibility.Id);
    }

    public async Task<CompatibilityResult> RemoveAsync(Guid partId, Guid compatibilityId, CancellationToken ct = default)
    {
        var part = await db.Parts.Include(p => p.Compatibilities).SingleOrDefaultAsync(p => p.Id == partId, ct);
        if (part is null || part.Compatibilities.All(c => c.Id != compatibilityId))
            return new CompatibilityResult(CompatibilityOutcome.NotFound, null, "Compatibilidade não encontrada.");
        part.RemoveCompatibility(compatibilityId, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return new CompatibilityResult(CompatibilityOutcome.Done, compatibilityId);
    }
}
