using Ecommerce.Application.Catalog;
using Ecommerce.Domain.Catalog;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ecommerce.Infrastructure.Catalog;

/// <summary>
/// Veículos doadores e configuração de rastreabilidade (RF10) da loja do escopo. Placa, chassi e Renavam são únicos na
/// loja (índices parciais); a corrida entre dois cadastros iguais cai no índice e vira <see cref="DonorOutcome.Duplicate"/>.
/// </summary>
public sealed class DonorVehicleService(TenantDbContext db, TimeProvider clock) : IDonorVehicles
{
    public async Task<DonorVehiclePage> SearchAsync(string? search, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var vehicles = db.DonorVehicles.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            var compact = new string(text.Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();
            var like = CatalogFilters.Contains(text);
            vehicles = vehicles.Where(v => (compact.Length >= 3 && (v.Plate!.Contains(compact) || v.Chassis!.Contains(compact) || v.Renavam!.Contains(compact)))
                || EF.Functions.ILike(v.Brand, like, "\\") || EF.Functions.ILike(v.Model, like, "\\"));
        }
        var total = await vehicles.CountAsync(ct);
        var items = await vehicles.OrderByDescending(v => v.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var ids = items.Select(v => v.Id).ToList();
        var counts = await db.Parts.AsNoTracking().Where(p => p.DonorVehicleId != null && ids.Contains(p.DonorVehicleId.Value))
            .GroupBy(p => p.DonorVehicleId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        return new DonorVehiclePage(items.Select(v => View(v, counts.GetValueOrDefault(v.Id), [])).ToList(), total, page, pageSize);
    }

    public async Task<DonorVehicleView?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var vehicle = await db.DonorVehicles.AsNoTracking().SingleOrDefaultAsync(v => v.Id == id, ct);
        if (vehicle is null) return null;
        var parts = await db.Parts.AsNoTracking().Where(p => p.DonorVehicleId == id).OrderBy(p => p.InternalCode)
            .Select(p => new DonorVehiclePart(p.Id, p.InternalCode, p.Title, p.Status, p.TraceabilityTag)).ToListAsync(ct);
        return View(vehicle, parts.Count, parts);
    }

    public async Task<DonorResult> CreateAsync(DonorVehicleDetails details, CancellationToken ct = default)
    {
        DonorVehicle vehicle;
        try { vehicle = DonorVehicle.Create(db.RequireTenantId(), details, clock.GetUtcNow()); }
        catch (ArgumentException e) { return Invalid(e); }
        if (await DuplicateAsync(vehicle, ct) is { } duplicate) return duplicate;
        db.DonorVehicles.Add(vehicle);
        return await SaveAsync(vehicle.Id, ct);
    }

    public async Task<DonorResult> UpdateAsync(Guid id, DonorVehicleDetails details, CancellationToken ct = default)
    {
        var vehicle = await db.DonorVehicles.SingleOrDefaultAsync(v => v.Id == id, ct);
        if (vehicle is null) return new DonorResult(DonorOutcome.NotFound);
        try { vehicle.Update(details, clock.GetUtcNow()); }
        catch (ArgumentException e) { return Invalid(e); }
        if (await DuplicateAsync(vehicle, ct) is { } duplicate) return duplicate;

        // Modo desmontadora: peças usadas à venda dependem destes dados (RF10 CA2).
        var settings = await db.StoreSettings.AsNoTracking().SingleOrDefaultAsync(ct);
        if (settings is { DismantlerMode: true } && vehicle.MissingForDismantler() is { Count: > 0 } missing
            && await db.Parts.AnyAsync(p => p.DonorVehicleId == id && p.Status == PartStatus.Active && p.Condition == PartCondition.Used, ct))
            return new DonorResult(DonorOutcome.Conflict, Message:
                $"Modo desmontadora: este veículo tem peças à venda e precisa de {string.Join(", ", missing)}. Inative as peças antes de tirar esses dados.");
        return await SaveAsync(id, ct);
    }

    public async Task<DonorResult> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var vehicle = await db.DonorVehicles.SingleOrDefaultAsync(v => v.Id == id, ct);
        if (vehicle is null) return new DonorResult(DonorOutcome.NotFound);
        if (await db.Parts.AnyAsync(p => p.DonorVehicleId == id, ct))
            return new DonorResult(DonorOutcome.Conflict, Message: "O veículo tem peças ligadas a ele: desligue as peças antes de excluir.");
        db.DonorVehicles.Remove(vehicle);
        await db.SaveChangesAsync(ct);
        return new DonorResult(DonorOutcome.Done);
    }

    public async Task<TraceabilitySettingsView> SettingsAsync(CancellationToken ct = default)
    {
        var settings = await db.StoreSettings.AsNoTracking().SingleOrDefaultAsync(ct);
        return new TraceabilitySettingsView(settings?.DismantlerMode ?? false, settings?.TraceabilityFields ?? []);
    }

    public async Task<TraceabilitySettingsView> SaveSettingsAsync(bool dismantlerMode, IReadOnlyList<TraceabilityField> fields, CancellationToken ct = default)
    {
        var tenantId = db.RequireTenantId();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO store_settings (tenant_id, reservation_minutes) VALUES ({tenantId}, {Domain.Inventory.StoreSettings.DefaultReservationMinutes})
            ON CONFLICT (tenant_id) DO NOTHING
            """, ct);
        var settings = await db.StoreSettings.SingleAsync(ct);
        settings.ConfigureTraceability(dismantlerMode, fields);
        await db.SaveChangesAsync(ct);
        return new TraceabilitySettingsView(settings.DismantlerMode, settings.TraceabilityFields);
    }

    private async Task<DonorResult?> DuplicateAsync(DonorVehicle vehicle, CancellationToken ct)
    {
        var others = db.DonorVehicles.AsNoTracking().Where(v => v.Id != vehicle.Id);
        if (vehicle.Plate is { } plate && await others.AnyAsync(v => v.Plate == plate, ct)) return Duplicate("esta placa");
        if (vehicle.Chassis is { } chassis && await others.AnyAsync(v => v.Chassis == chassis, ct)) return Duplicate("este chassi");
        if (vehicle.Renavam is { } renavam && await others.AnyAsync(v => v.Renavam == renavam, ct)) return Duplicate("este Renavam");
        return null;
    }

    private async Task<DonorResult> SaveAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Duplicate("a mesma placa, chassi ou Renavam");
        }
        return new DonorResult(DonorOutcome.Done, await GetAsync(id, ct));
    }

    private static DonorVehicleView View(DonorVehicle v, int partCount, IReadOnlyList<DonorVehiclePart> parts) =>
        new(v.Id, v.Label, v.Plate, v.Chassis, v.Renavam, v.Brand, v.Model, v.ModelYear, v.Color, v.DeregistrationCertificate,
            v.EntryInvoiceKey, v.EntryDate, v.Notes, v.MissingForDismantler(), partCount, parts, v.CreatedAt);

    private static DonorResult Duplicate(string what) => new(DonorOutcome.Duplicate, Message: $"Já existe um veículo doador com {what} na loja.");

    private static DonorResult Invalid(ArgumentException e) => new(DonorOutcome.Invalid, Message: e.Message.Split(" (Parameter", 2)[0]);
}
