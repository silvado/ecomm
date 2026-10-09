namespace Ecommerce.Application.Vehicles;

public sealed record VehicleOption(Guid Id, string Name);

public sealed record VehicleVersionOption(Guid Id, string Engine, int YearFrom, int? YearTo, bool Discontinued);

/// <summary>Tabela de veículos da plataforma (réplica <c>ref</c>), para a seleção em cascata: marca → modelo → versão.</summary>
public interface IVehicleCatalog
{
    Task<IReadOnlyList<VehicleOption>> BrandsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<VehicleOption>> ModelsAsync(Guid brandId, CancellationToken ct = default);

    /// <summary>Descontinuadas ficam de fora da seleção de novas compatibilidades.</summary>
    Task<IReadOnlyList<VehicleVersionOption>> VersionsAsync(Guid modelId, bool includeDiscontinued = false, CancellationToken ct = default);
}
