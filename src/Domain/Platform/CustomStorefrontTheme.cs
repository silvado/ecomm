using System.Text.RegularExpressions;

namespace Ecommerce.Domain.Platform;

/// <summary>
/// Tema exclusivo da loja personalizada (RF40 CA1): CSS e HTML de cabeçalho e rodapé cadastrados pela equipe da plataforma.
/// Fica no banco <c>plataforma</c> (ADR-0007) e chega aqui já limpo pelo sanitizador; a vitrine só aplica com a opção ligada.
/// </summary>
public sealed class TenantTheme
{
    public const int CssMaxLength = 100_000;
    public const int HtmlMaxLength = 50_000;

    private TenantTheme() { }

    public TenantTheme(Guid tenantId, DateTimeOffset now)
    {
        TenantId = tenantId;
        UpdatedAt = now;
    }

    public Guid TenantId { get; private set; }
    public string Css { get; private set; } = string.Empty;
    public string HeaderHtml { get; private set; } = string.Empty;
    public string FooterHtml { get; private set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; private set; }
    public string UpdatedBy { get; private set; } = string.Empty;

    public void Update(string? css, string? headerHtml, string? footerHtml, string updatedBy, DateTimeOffset now)
    {
        Css = Limit(css, CssMaxLength, "O CSS do tema pode ter no máximo 100.000 caracteres.");
        HeaderHtml = Limit(headerHtml, HtmlMaxLength, "O cabeçalho pode ter no máximo 50.000 caracteres.");
        FooterHtml = Limit(footerHtml, HtmlMaxLength, "O rodapé pode ter no máximo 50.000 caracteres.");
        UpdatedBy = updatedBy;
        UpdatedAt = now;
    }

    internal static string Limit(string? value, int max, string message)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length > max) throw new ArgumentException(message, nameof(value));
        return text;
    }
}

/// <summary>Página exclusiva da loja personalizada (RF40 CA1), servida em <c>/p/{slug}</c> quando publicada.</summary>
public sealed partial class TenantPage
{
    /// <summary>HIPÓTESE: limite para a equipe não transformar a vitrine num site sem fim.</summary>
    public const int MaxPerTenant = 20;
    public const int TitleMaxLength = 120;
    public const int SlugMaxLength = 60;

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,58}[a-z0-9])?$")]
    private static partial Regex SlugPattern();

    private TenantPage() { }

    public TenantPage(Guid tenantId, string slug, string title, string html, bool published, int position, string updatedBy, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        Update(slug, title, html, published, position, updatedBy, now);
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Slug { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public string Html { get; private set; } = string.Empty;
    public bool Published { get; private set; }

    /// <summary>Ordem nos links do rodapé.</summary>
    public int Position { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }
    public string UpdatedBy { get; private set; } = string.Empty;

    public static bool IsValidSlug(string? slug) =>
        slug is not null && SlugPattern().IsMatch(slug) && !slug.Contains("--", StringComparison.Ordinal);

    public void Update(string? slug, string? title, string? html, bool published, int position, string updatedBy, DateTimeOffset now)
    {
        var normalizedSlug = (slug ?? string.Empty).Trim().ToLowerInvariant();
        if (!IsValidSlug(normalizedSlug))
            throw new ArgumentException("Endereço da página inválido: letras minúsculas, números ou hífen (até 60), sem hífen nas pontas ou duplo.", nameof(slug));
        var normalizedTitle = (title ?? string.Empty).Trim();
        if (normalizedTitle.Length is 0 or > TitleMaxLength)
            throw new ArgumentException("O título da página é obrigatório (até 120 caracteres).", nameof(title));

        Slug = normalizedSlug;
        Title = normalizedTitle;
        Html = TenantTheme.Limit(html, TenantTheme.HtmlMaxLength, "O conteúdo da página pode ter no máximo 50.000 caracteres.");
        Published = published;
        Position = Math.Max(0, position);
        UpdatedBy = updatedBy;
        UpdatedAt = now;
    }
}
