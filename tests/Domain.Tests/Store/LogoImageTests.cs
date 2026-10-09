using System.Text;
using Ecommerce.Domain.Store;

namespace Ecommerce.Domain.Tests.Store;

public sealed class LogoImageTests
{
    public static byte[] Png => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];
    public static byte[] Jpeg => [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1];
    public static byte[] Webp => [.. "RIFF"u8, 0x24, 0, 0, 0, .. "WEBP"u8, .. "VP8 "u8];

    [Fact]
    public void Reconhece_png_jpeg_e_webp_pelo_conteudo()
    {
        Assert.Equal("image/png", LogoImage.DetectContentType(Png));
        Assert.Equal("image/jpeg", LogoImage.DetectContentType(Jpeg));
        Assert.Equal("image/webp", LogoImage.DetectContentType(Webp));
    }

    [Theory]
    [InlineData("<html><script>alert(1)</script></html>")]                     // HTML renomeado para .png
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"x()\"/>")] // SVG fica para depois (Q21)
    [InlineData("GIF89a......")]
    [InlineData("RIFF....WAVE")]                                               // RIFF que não é WebP
    [InlineData("")]
    public void Recusa_o_que_nao_for_png_jpeg_ou_webp(string content) =>
        Assert.Null(LogoImage.DetectContentType(Encoding.ASCII.GetBytes(content)));

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x3C, 0x00, 0x68, 0x00 })] // texto UTF-16 (BOM FF FE), não JPEG
    [InlineData(new byte[] { 0xFF, 0xD8, 0x00, 0x00 })]             // SOI sem o marcador seguinte
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00 })]       // começo de PNG incompleto
    public void Assinatura_parcial_nao_basta(byte[] header) => Assert.Null(LogoImage.DetectContentType(header));

    [Fact]
    public void Cabecalho_curto_nao_e_reconhecido_como_webp() =>
        Assert.Null(LogoImage.DetectContentType("RIFF"u8));

    [Fact]
    public void Chave_fica_sob_o_tenant_e_muda_a_cada_logo()
    {
        var tenant = Guid.NewGuid();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());

        Assert.StartsWith($"{tenant}/", LogoImage.StorageKey(tenant, a), StringComparison.Ordinal);
        Assert.NotEqual(LogoImage.StorageKey(tenant, a), LogoImage.StorageKey(tenant, b));
    }

    [Fact]
    public void Aparencia_so_aceita_os_formatos_reconhecidos()
    {
        var branding = StoreBranding.Default(Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentException>(() => branding.ReplaceLogo(Guid.NewGuid(), "image/svg+xml", DateTimeOffset.UtcNow));
        var first = Guid.NewGuid();
        Assert.Null(branding.ReplaceLogo(first, "image/png", DateTimeOffset.UtcNow));
        Assert.Equal(first, branding.ReplaceLogo(Guid.NewGuid(), "image/webp", DateTimeOffset.UtcNow));
    }
}
