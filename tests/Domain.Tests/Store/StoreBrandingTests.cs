using Ecommerce.Domain.Platform;
using Ecommerce.Domain.Store;

namespace Ecommerce.Domain.Tests.Store;

public sealed class StoreBrandingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("#000000", "#FFFFFF", 21.0)]
    [InlineData("#FFFFFF", "#FFFFFF", 1.0)]
    [InlineData("#777777", "#FFFFFF", 4.48)]   // cinza clássico que reprova no AA
    [InlineData("#767676", "#FFFFFF", 4.54)]   // o cinza mais claro que passa
    public void Contraste_segue_o_wcag(string a, string b, double expected) =>
        Assert.Equal(expected, HexColor.Parse(a).ContrastWith(HexColor.Parse(b)), precision: 2);

    [Theory]
    [InlineData("#1f5fbf", "#1F5FBF")]
    [InlineData(" #ABCDEF ", "#ABCDEF")]
    public void Cor_e_normalizada(string input, string expected) => Assert.Equal(expected, HexColor.Parse(input).Value);

    [Theory]
    [InlineData("1F5FBF")]
    [InlineData("#1F5FB")]
    [InlineData("#1F5FBG")]
    [InlineData("red")]
    [InlineData("")]
    public void Cor_invalida(string input) => Assert.False(HexColor.TryParse(input, out _));

    [Theory]
    [InlineData("#FFEB3B", "#000000")] // amarelo: texto preto
    [InlineData("#1F5FBF", "#FFFFFF")] // azul: texto branco
    public void Texto_do_botao_e_o_mais_legivel(string primary, string expected) =>
        Assert.Equal(expected, HexColor.Parse(primary).ReadableTextColor().Value);

    [Fact]
    public void Padrao_nao_gera_avisos() => Assert.Empty(StoreBranding.Default(Guid.NewGuid(), Now).ContrastWarnings());

    [Fact]
    public void Contraste_ruim_gera_aviso_mas_grava()
    {
        var branding = StoreBranding.Default(Guid.NewGuid(), Now);

        branding.Update("#FAFAFA", "#FFFFFF", "#AAAAAA", null, null, null, Now);

        Assert.Equal("#AAAAAA", branding.TextColor);
        Assert.Equal([ContrastWarning.TextOnBackground, ContrastWarning.PrimaryOnBackground], branding.ContrastWarnings());
    }

    [Fact]
    public void Textos_sao_aparados_e_limitados()
    {
        var branding = StoreBranding.Default(Guid.NewGuid(), Now);

        branding.Update("#1F5FBF", "#FFFFFF", "#1D2330", "  Sobre nós\r\nLinha 2  ", null, "Rodapé", Now);

        Assert.Equal("Sobre nós\nLinha 2", branding.About);
        Assert.Equal(string.Empty, branding.ReturnPolicy);
        Assert.Throws<ArgumentException>(() =>
            branding.Update("#1F5FBF", "#FFFFFF", "#1D2330", null, null, new string('x', StoreBranding.FooterMaxLength + 1), Now));
    }

    [Fact]
    public void Nome_fantasia_tem_limite()
    {
        var tenant = Tenant.Create("pecas-do-joao", Cnpj.Parse("11222333000181"), "Peças do João Ltda", "Peças do João", "plataforma.com.br", Now);

        tenant.Rename("  Peças do João Centro  ");

        Assert.Equal("Peças do João Centro", tenant.TradeName);
        Assert.Throws<ArgumentException>(() => tenant.Rename(new string('x', Tenant.TradeNameMaxLength + 1)));
        Assert.Throws<ArgumentException>(() => tenant.Rename("   "));
    }
}
