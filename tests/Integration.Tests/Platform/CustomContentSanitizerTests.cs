using Ecommerce.Infrastructure.Platform;

namespace Ecommerce.Integration.Tests.Platform;

/// <summary>RF40 CA1: o HTML e o CSS do tema exclusivo nunca levam script para a vitrine.</summary>
public sealed class CustomContentSanitizerTests
{
    private readonly CustomContentSanitizer sanitizer = new();

    [Theory]
    [InlineData("<script>alert(1)</script><p>ok</p>")]
    [InlineData("<img src=x onerror=alert(1)><p>ok</p>")]
    [InlineData("<a href=\"javascript:alert(1)\">ok</a>")]
    [InlineData("<a href=\"JaVaScRiPt:alert(1)\">ok</a>")]
    [InlineData("<a href=\"&#106;avascript:alert(1)\">ok</a>")]
    [InlineData("<a href=\"data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==\">ok</a>")]
    [InlineData("<iframe src=\"https://exemplo.com\"></iframe><p>ok</p>")]
    [InlineData("<form action=\"https://exemplo.com\"><input name=senha></form><p>ok</p>")]
    [InlineData("<svg><script>alert(1)</script></svg><p>ok</p>")]
    [InlineData("<math><mtext><table><mglyph><style><img src=x onerror=alert(1)></style></mglyph></table></mtext></math><p>ok</p>")]
    [InlineData("<div style=\"background:url(javascript:alert(1))\">ok</div>")]
    [InlineData("<p onclick=\"alert(1)\">ok</p>")]
    [InlineData("<object data=\"x.swf\"></object><embed src=\"x.swf\"><p>ok</p>")]
    [InlineData("<base href=\"https://exemplo.com/\"><meta http-equiv=\"refresh\" content=\"0;url=https://exemplo.com\"><p>ok</p>")]
    [InlineData("<noscript><p title=\"</noscript><img src=x onerror=alert(1)>\"></noscript><p>ok</p>")]
    public void HTML_perigoso_sai_limpo(string html)
    {
        var result = sanitizer.SanitizeHtml(html);

        foreach (var forbidden in new[] { "<script", "onerror", "onclick", "javascript:", "<iframe", "<form", "<input", "<svg", "<object", "<embed", "<base", "<meta", "style=", "data:" })
            Assert.DoesNotContain(forbidden, result.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ok", result.Value, StringComparison.Ordinal);
        Assert.NotEmpty(result.Removed);
    }

    [Fact]
    public void HTML_de_conteudo_passa_intacto()
    {
        const string html = "<section class=\"banner\" id=\"topo\"><h2>Peças usadas</h2><p>Garantia de <strong>90 dias</strong>.</p>"
            + "<img src=\"https://cdn.exemplo.com/banner.webp\" alt=\"Banner\" width=\"800\" loading=\"lazy\">"
            + "<a href=\"/busca?q=farol\">Faróis</a> <a href=\"https://wa.me/5548999990000\">WhatsApp</a> <a href=\"tel:+554899999000\">Ligue</a></section>";

        var result = sanitizer.SanitizeHtml(html);

        Assert.Equal(html, result.Value);
        Assert.Empty(result.Removed);
    }

    [Fact]
    public void Link_http_sem_criptografia_e_removido()
    {
        var result = sanitizer.SanitizeHtml("<a href=\"http://exemplo.com\">site</a>");

        Assert.DoesNotContain("http://", result.Value, StringComparison.Ordinal);
        Assert.Contains("atributo href em <a>", result.Removed);
    }

    [Fact]
    public void Diz_o_que_foi_removido()
    {
        var result = sanitizer.SanitizeHtml("<script>alert(1)</script><p onclick=\"x()\">ok</p>");

        Assert.Contains("tag <script>", result.Removed);
        Assert.Contains("atributo onclick em <p>", result.Removed);
    }

    [Fact]
    public void CSS_comum_passa()
    {
        var result = sanitizer.SanitizeCss("""
            h1 { color: #b00020; font-size: 2rem; }
            .banner { background-image: url("https://cdn.exemplo.com/fundo.webp"); padding: 1rem 2rem; }
            @media (max-width: 600px) { .banner { padding: 0.5rem; } }
            """);

        Assert.Contains("color", result.Value, StringComparison.Ordinal);
        Assert.Contains("https://cdn.exemplo.com/fundo.webp", result.Value, StringComparison.Ordinal);
        Assert.Contains("@media", result.Value, StringComparison.Ordinal);
        Assert.Empty(result.Removed);
    }

    [Theory]
    [InlineData("@import url(\"https://exemplo.com/x.css\"); h1 { color: red; }")]
    [InlineData("h1 { background: url(javascript:alert(1)); color: red; }")]
    [InlineData("h1 { background-image: url(\"http://exemplo.com/espiao.png\"); color: red; }")]
    [InlineData("h1 { behavior: url(x.htc); color: red; }")]
    [InlineData("h1 { width: expression(alert(1)); color: red; }")]
    [InlineData("h1 { -moz-binding: url(\"https://exemplo.com/x.xml#x\"); color: red; }")]
    public void CSS_perigoso_sai_limpo(string css)
    {
        var result = sanitizer.SanitizeCss(css);

        // Valor inválido (expression) some já na leitura do CSS, sem passar pela lista do que foi removido.
        foreach (var forbidden in new[] { "@import", "javascript", "http://", "behavior", "expression", "binding" })
            Assert.DoesNotContain(forbidden, result.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("color", result.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Diz_o_que_saiu_do_CSS()
    {
        var result = sanitizer.SanitizeCss("@import url(\"https://exemplo.com/x.css\"); h1 { behavior: url(x.htc); color: red; }");

        Assert.Contains("regra @import", result.Removed);
        Assert.Contains("propriedade behavior", result.Removed);
    }

    [Theory]
    [InlineData("h1 { color: red; } </style><script>alert(1)</script>")]
    [InlineData("h1::after { content: \"</style><script>alert(1)</script>\"; color: red; }")]
    public void CSS_nunca_fecha_a_tag_style(string css)
    {
        var result = sanitizer.SanitizeCss(css);

        Assert.DoesNotContain("<", result.Value, StringComparison.Ordinal);
    }
}
