using Ecommerce.Domain.Catalog;
using NetVips;
using Image = NetVips.Image;

namespace Ecommerce.Infrastructure.Catalog;

public sealed record ProcessedPhoto(IReadOnlyDictionary<int, byte[]> WebpBySize);

/// <summary>
/// Converte a foto enviada nos tamanhos publicados (RF08 CA3) com libvips (NetVips, MIT; libvips LGPL).
/// Segurança: só decodificadores confiáveis (<c>BlockUntrusted</c>), limite de pixels conferido pelo cabeçalho
/// antes de decodificar, falha em arquivo corrompido, orientação EXIF aplicada e todos os metadados removidos
/// (fotos de celular trazem GPS). Conversões simultâneas limitadas para não esgotar a memória da VPS.
/// Fontes: https://kleisauke.github.io/net-vips/ ; https://www.libvips.org/API/current/
/// </summary>
public static class PhotoProcessor
{
    private const int WebpQuality = 82;
    private static readonly SemaphoreSlim Slots = new(2);

    static PhotoProcessor()
    {
        NetVips.NetVips.BlockUntrusted = true;
        Cache.Max = 0; // imagens de usuários não ficam em cache de operações
    }

    /// <summary>Nulo + mensagem quando a imagem não pode ser usada (mensagem pronta para o usuário).</summary>
    public static async Task<(ProcessedPhoto? Photo, string? Error)> ProcessAsync(byte[] original, CancellationToken ct)
    {
        await Slots.WaitAsync(ct);
        try
        {
            return await Task.Run(() => Process(original), ct);
        }
        finally
        {
            Slots.Release();
        }
    }

    private static (ProcessedPhoto? Photo, string? Error) Process(byte[] original)
    {
        try
        {
            // Só o cabeçalho é lido aqui: largura e altura sem decodificar os pixels.
            using (var header = Image.NewFromBuffer(original, access: Enums.Access.Sequential, failOn: Enums.FailOn.Error))
            {
                if ((long)header.Width * header.Height > PartPhoto.MaxPixels)
                    return (null, $"Imagem grande demais: até {PartPhoto.MaxPixels / 1_000_000} megapixels.");
            }

            var sizes = PartPhoto.Sizes.OrderDescending().ToList();
            var result = new Dictionary<int, byte[]>();
            // A maior versão sai do original (aplicando a rotação EXIF); as menores, dela. A miniatura é lida em
            // sequência e só pode ser percorrida uma vez: fica na memória (até 1600×1600) para ser usada várias vezes.
            using var streamed = Image.ThumbnailBuffer(original, sizes[0], height: sizes[0], size: Enums.Size.Down);
            using var largest = streamed.CopyMemory();
            result[sizes[0]] = Save(largest);
            foreach (var size in sizes.Skip(1))
            {
                using var smaller = largest.ThumbnailImage(size, height: size, size: Enums.Size.Down);
                result[size] = Save(smaller);
            }
            return (new ProcessedPhoto(result), null);
        }
        catch (VipsException)
        {
            return (null, "Não foi possível ler a imagem: arquivo corrompido ou formato não suportado.");
        }
    }

    private static byte[] Save(Image image) => image.WebpsaveBuffer(q: WebpQuality, keep: Enums.ForeignKeep.None);
}
