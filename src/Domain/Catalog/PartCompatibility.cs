using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Catalog;

public enum CompatibilitySource
{
    Manual,
    AiSuggested,
    MlImport,
}

/// <summary>Faixa de anos e situação da versão do veículo, para validar a compatibilidade sem depender do agregado de veículos.</summary>
public sealed record VehicleVersionRange(Guid Id, int YearFrom, int? YearTo, bool Discontinued);

/// <summary>
/// "Esta peça serve neste veículo" (RF09 CA1). Sem anos = vale para a faixa inteira da versão; com anos, restringe
/// dentro dela (ex.: versão 2009–2012, peça só 2011–2012).
/// </summary>
public sealed class PartCompatibility : ITenantOwned
{
    private PartCompatibility() { }

    internal PartCompatibility(Guid tenantId, Guid partId, Guid vehicleVersionId, int? yearFrom, int? yearTo, CompatibilitySource source, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        PartId = partId;
        VehicleVersionId = vehicleVersionId;
        YearFrom = yearFrom;
        YearTo = yearTo;
        Source = source;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid PartId { get; private set; }
    public Guid VehicleVersionId { get; private set; }
    public int? YearFrom { get; private set; }
    public int? YearTo { get; private set; }
    public CompatibilitySource Source { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
