namespace Ecommerce.Application.Platform;

/// <summary>Conteúdo depois da limpeza e o que foi removido (texto curto para a equipe entender o que sumiu).</summary>
public sealed record SanitizedContent(string Value, IReadOnlyList<string> Removed);

/// <summary>
/// Limpeza do HTML e do CSS do tema exclusivo (RF40 CA1): lista do que é permitido; nunca script, evento, iframe,
/// formulário ou link <c>javascript:</c>. Roda no servidor ao salvar e na prévia.
/// </summary>
public interface ICustomContentSanitizer
{
    SanitizedContent SanitizeHtml(string? html);

    SanitizedContent SanitizeCss(string? css);
}

public sealed record PlatformThemePage(
    Guid Id, string Slug, string Title, string Html, bool Published, int Position, DateTimeOffset UpdatedAt, string UpdatedBy);

/// <summary>Tema e páginas da loja como a equipe edita (já limpos). <c>UpdatedAt</c> nulo = tema nunca salvo.</summary>
public sealed record PlatformTheme(
    Guid TenantId, string TradeName, bool CustomStorefront, string Css, string HeaderHtml, string FooterHtml,
    DateTimeOffset? UpdatedAt, string? UpdatedBy, IReadOnlyList<PlatformThemePage> Pages);

public sealed record SavePlatformTheme(string? Css, string? HeaderHtml, string? FooterHtml);

public sealed record SavePlatformPage(string? Slug, string? Title, string? Html, bool Published, int Position);

/// <summary>Prévia: o que seria gravado, sem gravar.</summary>
public sealed record PlatformThemePreview(string Css, string HeaderHtml, string FooterHtml, IReadOnlyList<string> Removed);

/// <summary>Resultado de salvar: o tema atualizado e o que a limpeza removeu, ou a mensagem para a equipe.</summary>
public sealed record PlatformThemeResult(PlatformTheme? Theme, IReadOnlyList<string> Removed, string? Error, bool NotFound = false)
{
    public static PlatformThemeResult Missing(string error) => new(null, [], error, NotFound: true);

    public static PlatformThemeResult Invalid(string error) => new(null, [], error);
}

/// <summary>
/// Tema e páginas exclusivos da loja personalizada (RF40 CA1), pelo superadmin. Só banco <c>plataforma</c> (ADR-0007);
/// toda gravação vai para a auditoria. Pode ser preparado antes de ligar a opção: a vitrine só aplica com ela ligada.
/// </summary>
public interface IPlatformThemeService
{
    Task<PlatformTheme?> GetAsync(Guid tenantId, CancellationToken ct = default);

    PlatformThemePreview Preview(SavePlatformTheme theme);

    Task<PlatformThemeResult> SaveThemeAsync(Guid tenantId, SavePlatformTheme theme, CancellationToken ct = default);

    Task<PlatformThemeResult> CreatePageAsync(Guid tenantId, SavePlatformPage page, CancellationToken ct = default);

    Task<PlatformThemeResult> UpdatePageAsync(Guid tenantId, Guid pageId, SavePlatformPage page, CancellationToken ct = default);

    Task<PlatformThemeResult> DeletePageAsync(Guid tenantId, Guid pageId, CancellationToken ct = default);
}
