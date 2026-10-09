namespace Ecommerce.Application.Catalog;

public enum CompatibilityOutcome
{
    Done,
    NotFound,
    /// <summary>Já cadastrada ou limite atingido.</summary>
    Conflict,
    Invalid,
}

/// <summary><see cref="Message"/> vem pronta para o usuário quando o resultado não é <see cref="CompatibilityOutcome.Done"/>.</summary>
public sealed record CompatibilityResult(CompatibilityOutcome Outcome, Guid? CompatibilityId = null, string? Message = null);

/// <summary>Veículos em que a peça serve (RF09 CA1), na loja do escopo.</summary>
public interface IPartCompatibilities
{
    /// <summary>Anos nulos = faixa inteira da versão; informados, precisam estar dentro dela.</summary>
    Task<CompatibilityResult> AddAsync(Guid partId, Guid vehicleVersionId, int? yearFrom, int? yearTo, CancellationToken ct = default);

    Task<CompatibilityResult> RemoveAsync(Guid partId, Guid compatibilityId, CancellationToken ct = default);
}
