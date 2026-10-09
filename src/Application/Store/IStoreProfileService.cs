using Ecommerce.Domain.Store;

namespace Ecommerce.Application.Store;

public sealed record StoreTheme(string PrimaryColor, string OnPrimaryColor, string BackgroundColor, string TextColor);

public sealed record StoreTexts(string About, string ReturnPolicy, string Footer);

/// <summary>Dados da loja vistos pelo Dono no painel (RF01).</summary>
public sealed record StoreProfile(
    string Slug, string Cnpj, string LegalName, string TradeName,
    StoreTheme Theme, StoreTexts Texts, IReadOnlyList<ContrastWarning> Warnings);

public sealed record UpdateStoreProfile(
    string TradeName, string PrimaryColor, string BackgroundColor, string TextColor,
    string? About, string? ReturnPolicy, string? Footer);

/// <summary>O que a loja pública precisa para se desenhar (storefront SSR).</summary>
public sealed record PublicStore(string Slug, string Name, StoreTheme Theme, StoreTexts Texts);

/// <summary>Perfil e aparência da loja do tenant do escopo.</summary>
public interface IStoreProfileService
{
    Task<StoreProfile> GetAsync(CancellationToken ct = default);

    /// <summary>Erro de validação vem como mensagem pronta para o usuário; contraste ruim é só aviso no perfil.</summary>
    Task<(StoreProfile? Profile, string? Error)> UpdateAsync(UpdateStoreProfile update, CancellationToken ct = default);

    /// <summary>Com cache curto (RF01 CA3: mudança visível em até 1 min); invalidado ao salvar.</summary>
    Task<PublicStore> GetPublicAsync(CancellationToken ct = default);
}
