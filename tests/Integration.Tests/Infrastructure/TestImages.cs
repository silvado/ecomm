using NetVips;

namespace Ecommerce.Integration.Tests.Infrastructure;

/// <summary>Imagens geradas na hora (nada de binário no repositório).</summary>
public static class TestImages
{
    /// <summary>JPEG com faixas de cor (comprime pouco, como uma foto); <paramref name="orientation"/> grava a tag EXIF.</summary>
    public static byte[] Jpeg(int width, int height, int? orientation = null)
    {
        using var gradient = Image.Xyz(width, height)[0].Linear([255.0 / width], [0.0]).Cast(Enums.BandFormat.Uchar);
        using var rgb = gradient.Bandjoin(gradient.Invert(), gradient);
        if (orientation is null) return rgb.JpegsaveBuffer(q: 85);
        using var tagged = rgb.Mutate(m => m.Set(GValue.GIntType, "orientation", orientation.Value));
        return tagged.JpegsaveBuffer(q: 85);
    }

    public static byte[] Png(int width, int height)
    {
        using var black = Image.Black(width, height, bands: 3);
        return black.PngsaveBuffer();
    }

    /// <summary>"Bomba": poucos KB no disco, muitos megapixels quando decodificada.</summary>
    public static byte[] HugeBlackJpeg(int width, int height)
    {
        using var black = Image.Black(width, height, bands: 3);
        return black.JpegsaveBuffer(q: 50);
    }

    /// <summary>Cabeçalho JPEG válido seguido de lixo.</summary>
    public static byte[] CorruptJpeg()
    {
        var bytes = Jpeg(400, 300);
        for (var i = 200; i < bytes.Length; i++) bytes[i] = (byte)(i * 31);
        return bytes;
    }

    public static (int Width, int Height, string[] Fields) Inspect(byte[] encoded)
    {
        using var image = Image.NewFromBuffer(encoded);
        return (image.Width, image.Height, image.GetFields());
    }
}
