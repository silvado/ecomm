using Ecommerce.Domain.Catalog;

namespace Ecommerce.Application.Catalog;

// Rastreabilidade de peça usada (RF10): veículos doadores, configuração da loja (modo desmontadora e campos extras) e a
// origem de cada peça. Tudo só no painel — nada disso vai para a vitrine nem para os canais (CA3).

/// <summary>Origem da peça: veículo doador (da mesma loja), etiqueta e campos extras configurados.</summary>
public sealed record SetPartTraceability(Guid PartId, Guid? DonorVehicleId, string? Tag, IReadOnlyDictionary<string, string?>? CustomFields);

public sealed record DonorVehiclePart(Guid Id, string InternalCode, string Title, PartStatus Status, string? TraceabilityTag);

public sealed record DonorVehicleView(
    Guid Id, string Label, string? Plate, string? Chassis, string? Renavam, string Brand, string Model, int? ModelYear, string? Color,
    string? DeregistrationCertificate, string? EntryInvoiceKey, DateOnly? EntryDate, string? Notes,
    IReadOnlyList<string> MissingForDismantler, int PartCount, IReadOnlyList<DonorVehiclePart> Parts, DateTimeOffset CreatedAt);

public sealed record DonorVehiclePage(IReadOnlyList<DonorVehicleView> Items, int Total, int Page, int PageSize);

public enum DonorOutcome
{
    Done,
    NotFound,
    Invalid,
    /// <summary>Placa, chassi ou Renavam já cadastrados em outro veículo da loja.</summary>
    Duplicate,
    /// <summary>Ação bloqueada pelo estado atual (ex.: excluir veículo com peças; deixar incompleto com peças à venda no modo desmontadora).</summary>
    Conflict,
}

public sealed record DonorResult(DonorOutcome Outcome, DonorVehicleView? Vehicle = null, string? Message = null);

public sealed record TraceabilitySettingsView(bool DismantlerMode, IReadOnlyList<TraceabilityField> Fields);

/// <summary>Veículos doadores e configuração de rastreabilidade da loja do escopo (RLS + filtro global).</summary>
public interface IDonorVehicles
{
    /// <param name="search">Placa, chassi, Renavam, marca ou modelo.</param>
    Task<DonorVehiclePage> SearchAsync(string? search, int page, int pageSize, CancellationToken ct = default);

    Task<DonorVehicleView?> GetAsync(Guid id, CancellationToken ct = default);

    Task<DonorResult> CreateAsync(DonorVehicleDetails details, CancellationToken ct = default);

    Task<DonorResult> UpdateAsync(Guid id, DonorVehicleDetails details, CancellationToken ct = default);

    /// <summary>Só sem peças ligadas: a origem de uma peça não pode sumir.</summary>
    Task<DonorResult> DeleteAsync(Guid id, CancellationToken ct = default);

    Task<TraceabilitySettingsView> SettingsAsync(CancellationToken ct = default);

    /// <exception cref="ArgumentException">Campos inválidos (mensagem para o usuário).</exception>
    Task<TraceabilitySettingsView> SaveSettingsAsync(bool dismantlerMode, IReadOnlyList<TraceabilityField> fields, CancellationToken ct = default);
}
