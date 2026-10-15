using Ecommerce.Application.Storage;
using Ecommerce.Domain.Store;

namespace Ecommerce.Application.Store;

public sealed record StoreTheme(string PrimaryColor, string OnPrimaryColor, string BackgroundColor, string TextColor);

public sealed record StoreTexts(string About, string ReturnPolicy, string Footer);

/// <summary>Dados da loja vistos pelo Dono no painel (RF01).</summary>
public sealed record StoreProfile(
    string Slug, string Cnpj, string LegalName, string TradeName,
    StoreTheme Theme, StoreTexts Texts, IReadOnlyList<ContrastWarning> Warnings, Guid? LogoId, bool HideOutOfStock, StoreShipping Shipping,
    bool CustomStorefront = false);

/// <summary>
/// Loja personalizada (RF40): a aparência (logo, nome, cores, textos) é cuidada pela equipe da plataforma e não muda pelo
/// painel. Frete, retirada e estoque continuam com o Dono.
/// </summary>
public sealed class AppearanceLockedException() : InvalidOperationException(Text)
{
    public const string Text ="Loja personalizada: a aparência (logo, nome, cores e textos) é cuidada pela equipe da plataforma. Peça a mudança ao suporte.";
}

/// <summary>Frete da loja (RF14): CEP de onde saem as entregas e retirada no balcão.</summary>
public sealed record StoreShipping(string? OriginPostalCode, bool PickupEnabled, string? PickupAddress);

/// <param name="HideOutOfStock">RF13 CA4; nulo = mantém a configuração atual.</param>
/// <param name="Shipping">RF14; nulo = mantém a configuração atual.</param>
public sealed record UpdateStoreProfile(
    string TradeName, string PrimaryColor, string BackgroundColor, string TextColor,
    string? About, string? ReturnPolicy, string? Footer, bool? HideOutOfStock = null, StoreShipping? Shipping = null);

/// <summary>
/// O que a loja pública precisa para se desenhar (storefront SSR). <see cref="LogoUrl"/> é relativo ao domínio
/// da loja e muda a cada troca de logo (pode ter cache longo).
/// </summary>
/// <param name="PrimaryHost">Host principal: a vitrine redireciona (301) os outros hosts da loja para ele (RF02 CA3, RF03).</param>
public sealed record PublicStore(string Slug, string Name, StoreTheme Theme, StoreTexts Texts, string? LogoUrl, string? PrimaryHost = null);

/// <summary>Política de privacidade e termos como a vitrine mostra: os da loja ou, se vazios, o modelo padrão (RNF04).</summary>
public sealed record StoreLegalTexts(string PrivacyPolicy, string Terms);

/// <summary>Para o painel: o que a loja escreveu (vazio = usa o modelo) e o modelo já preenchido com os dados dela.</summary>
public sealed record StoreLegalTextsEditor(string PrivacyPolicy, string Terms, string DefaultPrivacyPolicy, string DefaultTerms);

/// <summary>Perfil e aparência da loja do tenant do escopo.</summary>
public interface IStoreProfileService
{
    /// <summary>Caminho público do logo, servido pela API no domínio da loja.</summary>
    public static string LogoPath(Guid logoId) => $"/api/loja/logo/{logoId}";

    Task<StoreProfile> GetAsync(CancellationToken ct = default);

    /// <summary>Erro de validação vem como mensagem pronta para o usuário; contraste ruim é só aviso no perfil.</summary>
    Task<(StoreProfile? Profile, string? Error)> UpdateAsync(UpdateStoreProfile update, CancellationToken ct = default);

    /// <summary>Com cache curto (RF01 CA3: mudança visível em até 1 min); invalidado ao salvar.</summary>
    Task<PublicStore> GetPublicAsync(CancellationToken ct = default);

    /// <summary>RF01 CA2: PNG, JPEG ou WebP até 2 MB, reconhecido pelo conteúdo. Erro vem pronto para o usuário.</summary>
    Task<(StoreProfile? Profile, string? Error)> ReplaceLogoAsync(Stream content, CancellationToken ct = default);

    Task<StoreProfile> RemoveLogoAsync(CancellationToken ct = default);

    Task<StoreLegalTextsEditor> GetLegalTextsAsync(CancellationToken ct = default);

    /// <summary>Texto vazio volta ao modelo padrão. Vale também com a loja personalizada (não é aparência).</summary>
    Task<(StoreLegalTextsEditor? Texts, string? Error)> UpdateLegalTextsAsync(string? privacyPolicy, string? terms, CancellationToken ct = default);

    Task<StoreLegalTexts> GetPublicLegalTextsAsync(CancellationToken ct = default);

    /// <summary>
    /// O logo da loja do escopo, se <paramref name="logoId"/> for o atual (nulo = o atual, para o painel).
    /// Id de outra loja ou de um logo já trocado não é encontrado.
    /// </summary>
    Task<StoredFile?> GetLogoAsync(Guid? logoId = null, CancellationToken ct = default);
}
