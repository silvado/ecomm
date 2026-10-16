using AngleSharp.Css.Dom;
using Ecommerce.Application.Platform;
using Ganss.Xss;

namespace Ecommerce.Infrastructure.Platform;

/// <summary>
/// Limpeza do tema exclusivo (RF40 CA1) com o HtmlSanitizer (https://github.com/mganss/HtmlSanitizer), por lista do que é
/// permitido. HTML: tags de conteúdo, sem <c>style</c> inline (o visual vem do CSS do tema), links só https, mailto, tel
/// ou relativos. CSS: propriedades da lista padrão da biblioteca, só <c>@media</c>, <c>url()</c> só https ou relativo;
/// <c>&lt;</c> escapado para o texto nunca fechar o <c>&lt;style&gt;</c> da página.
/// </summary>
public sealed class CustomContentSanitizer : ICustomContentSanitizer
{
    private const int MaxRemovedListed = 30;

    private static readonly string[] HtmlTags =
    [
        "a", "abbr", "address", "article", "aside", "b", "blockquote", "br", "caption", "cite", "code", "dd", "div", "dl", "dt",
        "em", "figcaption", "figure", "footer", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "i", "img", "li", "mark",
        "nav", "ol", "p", "pre", "s", "section", "small", "span", "strong", "sub", "sup", "table", "tbody", "td", "tfoot",
        "th", "thead", "time", "tr", "u", "ul",
    ];

    private static readonly string[] HtmlAttributes =
    [
        "class", "id", "href", "src", "alt", "title", "width", "height", "loading", "colspan", "rowspan", "datetime",
        "aria-label", "aria-hidden", "role",
    ];

    private static readonly string[] Schemes = ["https", "mailto", "tel"];

    public SanitizedContent SanitizeHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return new SanitizedContent(string.Empty, []);

        var sanitizer = new HtmlSanitizer(new HtmlSanitizerOptions
        {
            AllowedTags = new HashSet<string>(HtmlTags, StringComparer.OrdinalIgnoreCase),
            AllowedAttributes = new HashSet<string>(HtmlAttributes, StringComparer.OrdinalIgnoreCase),
            AllowedSchemes = new HashSet<string>(Schemes, StringComparer.OrdinalIgnoreCase),
            UriAttributes = new HashSet<string>(["href", "src"], StringComparer.OrdinalIgnoreCase),
            AllowedCssProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AllowedAtRules = new HashSet<CssRuleType>(),
        });
        var removed = new Removed();
        sanitizer.RemovingTag += (_, e) => removed.Add($"tag <{e.Tag.LocalName}>");
        sanitizer.RemovingAttribute += (_, e) => removed.Add($"atributo {e.Attribute.Name} em <{e.Tag.LocalName}>");
        sanitizer.RemovingStyle += (_, e) => removed.Add($"estilo {e.Style.Name} em <{e.Tag.LocalName}>");

        return new SanitizedContent(sanitizer.Sanitize(html).Trim(), removed.Items);
    }

    public SanitizedContent SanitizeCss(string? css)
    {
        if (string.IsNullOrWhiteSpace(css)) return new SanitizedContent(string.Empty, []);

        // A biblioteca limpa folhas de estilo dentro de <style>: só essa tag é aceita e o texto volta já reescrito.
        var sanitizer = new HtmlSanitizer(new HtmlSanitizerOptions
        {
            AllowedTags = new HashSet<string>(["style"], StringComparer.OrdinalIgnoreCase),
            AllowedAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AllowedSchemes = new HashSet<string>(["https"], StringComparer.OrdinalIgnoreCase),
            AllowedAtRules = new HashSet<CssRuleType>([CssRuleType.Style, CssRuleType.Media]),
            AllowedCssProperties = new HashSet<string>(HtmlSanitizerDefaults.AllowedCssProperties, StringComparer.OrdinalIgnoreCase),
            UriAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AllowCssCustomProperties = true,
        });
        var removed = new Removed();
        sanitizer.RemovingStyle += (_, e) => removed.Add($"propriedade {e.Style.Name}");
        sanitizer.RemovingAtRule += (_, e) => removed.Add($"regra {AtRuleName(e.Rule)}");
        sanitizer.RemovingTag += (_, e) => removed.Add($"tag <{e.Tag.LocalName}>");

        // "</style" no texto encerraria a tag antes da hora e o resto viraria HTML: escapado antes de passar pela limpeza.
        var dom = sanitizer.SanitizeDom($"<style>{css.Replace("<", "\\3c ", StringComparison.Ordinal)}</style>");
        var text = dom.QuerySelector("style")?.TextContent ?? string.Empty;
        return new SanitizedContent(EscapeMarkup(text).Trim(), removed.Items);
    }

    /// <summary>Nenhum <c>&lt;</c> sai no CSS: escape válido em string e identificador (<c>\3c </c>).</summary>
    public static string EscapeMarkup(string css) => css.Replace("<", "\\3c ", StringComparison.Ordinal);

    private static string AtRuleName(ICssRule rule)
    {
        var text = rule.CssText;
        var end = text.IndexOfAny([' ', '{', ';', '(']);
        return end > 0 ? text[..end] : rule.Type.ToString();
    }

    private sealed class Removed
    {
        private readonly List<string> items = [];

        public IReadOnlyList<string> Items => items;

        public void Add(string item)
        {
            if (items.Count < MaxRemovedListed && !items.Contains(item)) items.Add(item);
        }
    }
}
