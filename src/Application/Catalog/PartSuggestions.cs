using Ecommerce.Domain.Catalog;

namespace Ecommerce.Application.Catalog;

/// <summary>
/// Versão da tabela de veículos que a IA sugeriu (RF30 CA1). Anos <see cref="YearFrom"/>/<see cref="YearTo"/> nulos = versão
/// inteira; informados, restringem dentro dela — prontos para <see cref="IPartCompatibilities.AddAsync"/>.
/// </summary>
public sealed record SuggestedCompatibility(
    Guid VehicleVersionId, string Brand, string Model, string Engine, int VersionYearFrom, int? VersionYearTo, int? YearFrom, int? YearTo);

/// <summary>
/// Sugestão de cadastro a partir das fotos (RF30). Nada é gravado: o painel preenche o formulário e a pessoa revisa, digita o
/// preço e salva (CA2; RF35 — a IA nunca define preço). <see cref="UnmatchedVehicles"/>: veículos citados pela IA que não estão
/// na tabela da plataforma — só texto, para a pessoa escolher à mão (a IA não cria vínculo com veículo inexistente).
/// </summary>
public sealed record PartSuggestion(
    string Title, string Description, PartCondition Condition, IReadOnlyList<string> OemCodes,
    IReadOnlyList<SuggestedCompatibility> Compatibilities, IReadOnlyList<string> UnmatchedVehicles);

public enum SuggestionOutcome
{
    Done,
    /// <summary>Fotos ou texto inválidos.</summary>
    Invalid,
    /// <summary>Limite mensal do plano atingido (RF36 CA2).</summary>
    QuotaExceeded,
    /// <summary>IA fora do ar ou desligada. <see cref="PartSuggestionResult.Transient"/>: vale tentar de novo.</summary>
    Unavailable,
    /// <summary>A IA respondeu algo que não serve (recusa, cortada): descartado.</summary>
    Unusable,
}

/// <summary><see cref="Message"/> vem pronta para o usuário quando o resultado não é <see cref="SuggestionOutcome.Done"/>.</summary>
public sealed record PartSuggestionResult(SuggestionOutcome Outcome, PartSuggestion? Suggestion = null, string? Message = null, bool Transient = false);

/// <summary>Uma foto como chegou do navegador (bytes do arquivo).</summary>
public sealed record SuggestionPhoto(byte[] Content);

/// <summary>Cadastro assistido por IA (RF30) na loja do escopo. Consome 1 unidade do limite do plano só quando dá certo (CA4).</summary>
public interface IPartSuggestions
{
    public const int MaxPhotos = 10;
    public const int MaxNotesLength = 500;

    /// <param name="notes">O que a pessoa já sabe (código OEM, veículo de origem, lado…); opcional.</param>
    Task<PartSuggestionResult> SuggestAsync(IReadOnlyList<SuggestionPhoto> photos, string? notes, CancellationToken ct = default);
}
