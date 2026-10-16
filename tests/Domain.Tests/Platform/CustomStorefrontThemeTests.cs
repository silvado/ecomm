using Ecommerce.Domain.Platform;

namespace Ecommerce.Domain.Tests.Platform;

/// <summary>RF40 CA1: regras do tema e das páginas exclusivos.</summary>
public sealed class CustomStorefrontThemeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("quem-somos", true)]
    [InlineData("a", true)]
    [InlineData("promocoes-2026", true)]
    [InlineData("-quem", false)]
    [InlineData("quem-", false)]
    [InlineData("quem--somos", false)]
    [InlineData("Quem", false)]
    [InlineData("../admin", false)]
    [InlineData("quem somos", false)]
    [InlineData("", false)]
    public void Endereco_da_pagina(string slug, bool valid) => Assert.Equal(valid, TenantPage.IsValidSlug(slug));

    [Fact]
    public void Endereco_vira_minusculo_e_titulo_e_obrigatorio()
    {
        var page = new TenantPage(Guid.NewGuid(), " Quem-Somos ", " Quem somos ", "<p>x</p>", true, -3, "sa@plataforma.test", Now);

        Assert.Equal(("quem-somos", "Quem somos", 0), (page.Slug, page.Title, page.Position));
        Assert.Throws<ArgumentException>(() => page.Update("quem-somos", " ", "", true, 0, "sa@plataforma.test", Now));
        Assert.Throws<ArgumentException>(() => page.Update("quem-somos", new string('x', TenantPage.TitleMaxLength + 1), "", true, 0, "sa@plataforma.test", Now));
    }

    [Fact]
    public void Tema_recusa_conteudo_grande_demais()
    {
        var theme = new TenantTheme(Guid.NewGuid(), Now);

        Assert.Throws<ArgumentException>(() => theme.Update(new string('a', TenantTheme.CssMaxLength + 1), "", "", "sa@plataforma.test", Now));
        Assert.Throws<ArgumentException>(() => theme.Update("", new string('a', TenantTheme.HtmlMaxLength + 1), "", "sa@plataforma.test", Now));

        theme.Update(" h1 { color: red } ", null, null, "sa@plataforma.test", Now.AddMinutes(1));
        Assert.Equal(("h1 { color: red }", "", "sa@plataforma.test", Now.AddMinutes(1)), (theme.Css, theme.HeaderHtml, theme.UpdatedBy, theme.UpdatedAt));
    }
}
