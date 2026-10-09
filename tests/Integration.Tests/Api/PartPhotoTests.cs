using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Ecommerce.Application.Storage;
using Ecommerce.Domain.Catalog;
using Ecommerce.Domain.Identity;
using Ecommerce.Infrastructure.Storage;
using Ecommerce.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>RF08 CA1/CA3 — fotos da peça: conversão em WebP, limites, ordem, remoção e isolamento.</summary>
public sealed class PartPhotoTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record PhotoDto(Guid Id, int Position);
    private sealed record PartDto(Guid Id, string Status, List<PhotoDto> Photos);
    private sealed record SummaryDto(Guid Id, Guid? CoverPhotoId);
    private sealed record PageDto(List<SummaryDto> Items);

    private InMemoryFileStorage Storage => api.Services.GetRequiredService<InMemoryFileStorage>();

    private async Task<(Ecommerce.Domain.Platform.Tenant Tenant, string Token, Guid PartId)> PartAsync(TenantRole role = TenantRole.Owner)
    {
        var tenant = await api.CreateTenantAsync();
        var token = await api.SessionForAsync(tenant, role);
        var body = new Dictionary<string, object?>
        {
            ["internalCode"] = "R-" + Guid.NewGuid().ToString("N")[..8], ["title"] = "Retrovisor direito", ["condition"] = "used",
            ["price"] = 180m, ["quantity"] = 1, ["oemCodes"] = Array.Empty<string>(),
        };
        var created = await SendAsync(api.PanelClient(), token, HttpMethod.Post, "/api/painel/pecas", body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (tenant, token, (await created.Content.ReadFromJsonAsync<PartDto>())!.Id);
    }

    private async Task<HttpResponseMessage> UploadAsync(string token, Guid partId, byte[] bytes, string declared = "image/jpeg")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/painel/pecas/{partId}/fotos") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(declared);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await api.PanelClient().SendAsync(request);
    }

    private async Task<Guid> UploadOkAsync(string token, Guid partId, byte[] bytes)
    {
        var response = await UploadAsync(token, partId, bytes);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PartDto>())!.Photos.OrderBy(p => p.Position).Last().Id;
    }

    private byte[] Stored(StorageArea area, string key)
    {
        var file = Storage.GetAsync(area, key).GetAwaiter().GetResult()!;
        using var copy = new MemoryStream();
        file.Content.CopyTo(copy);
        return copy.ToArray();
    }

    [Fact]
    public async Task Foto_vira_webp_em_tres_tamanhos_e_o_original_fica_no_privado()
    {
        var (tenant, token, partId) = await PartAsync();
        var original = TestImages.Jpeg(3000, 2000);

        var photoId = await UploadOkAsync(token, partId, original);

        Assert.Equal(original, Stored(StorageArea.Private, PartPhoto.OriginalKey(tenant.Id, partId, photoId)));
        foreach (var (size, expected) in new[] { (1600, (1600, 1067)), (800, (800, 534)), (300, (300, 200)) })
        {
            var webp = Stored(StorageArea.Public, PartPhoto.PublicKey(tenant.Id, partId, photoId, size));
            Assert.Equal("image/webp", Ecommerce.Domain.Common.ImageSignature.DetectContentType(webp));
            var (w, h, _) = TestImages.Inspect(webp);
            Assert.Equal(expected, (w, h));
        }
        var list = await (await GetAsync(api.PanelClient(), token, "/api/painel/pecas")).Content.ReadFromJsonAsync<PageDto>();
        Assert.Equal(photoId, list!.Items.Single(p => p.Id == partId).CoverPhotoId);
    }

    [Fact]
    public async Task Orientacao_exif_e_aplicada_e_metadados_sao_removidos()
    {
        var (tenant, token, partId) = await PartAsync();

        var photoId = await UploadOkAsync(token, partId, TestImages.Jpeg(2000, 1000, orientation: 6)); // celular em pé

        var (w, h, fields) = TestImages.Inspect(Stored(StorageArea.Public, PartPhoto.PublicKey(tenant.Id, partId, photoId, 1600)));
        Assert.Equal((800, 1600), (w, h));
        Assert.DoesNotContain("exif-data", fields);
        Assert.DoesNotContain("orientation", fields);
    }

    [Fact]
    public async Task Foto_pequena_nao_e_ampliada()
    {
        var (tenant, token, partId) = await PartAsync();

        var photoId = await UploadOkAsync(token, partId, TestImages.Png(200, 100));

        var (w, h, _) = TestImages.Inspect(Stored(StorageArea.Public, PartPhoto.PublicKey(tenant.Id, partId, photoId, 1600)));
        Assert.Equal((200, 100), (w, h));
    }

    [Fact]
    public async Task Imagens_perigosas_ou_invalidas_sao_recusadas_sem_gravar_nada()
    {
        var (_, token, partId) = await PartAsync();
        var before = Storage.Count;
        var oversized = new byte[PartPhoto.MaxUploadBytes + 1];
        TestImages.Jpeg(10, 10).CopyTo(oversized, 0);

        Assert.Equal("Imagem grande demais: até 40 megapixels.", await TitleAsync(await UploadAsync(token, partId, TestImages.HugeBlackJpeg(8000, 6000))));
        Assert.Equal("Formato não aceito: envie JPG, PNG ou WebP.",
            await TitleAsync(await UploadAsync(token, partId, Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>"))));
        Assert.Equal("A foto pode ter no máximo 10 MB.", await TitleAsync(await UploadAsync(token, partId, oversized)));
        Assert.Equal("Não foi possível ler a imagem: arquivo corrompido ou formato não suportado.",
            await TitleAsync(await UploadAsync(token, partId, TestImages.CorruptJpeg())));

        Assert.Equal(before, Storage.Count);
        Assert.Empty((await (await GetAsync(api.PanelClient(), token, $"/api/painel/pecas/{partId}")).Content.ReadFromJsonAsync<PartDto>())!.Photos);
    }

    [Fact]
    public async Task Limite_de_20_fotos_vale_tambem_para_envios_simultaneos()
    {
        var (_, token, partId) = await PartAsync();
        var small = TestImages.Jpeg(120, 80);
        for (var i = 0; i < Part.MaxPhotos - 2; i++) await UploadOkAsync(token, partId, small);

        var parallel = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => UploadAsync(token, partId, small))));

        Assert.Equal(2, parallel.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(parallel.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        var part = await (await GetAsync(api.PanelClient(), token, $"/api/painel/pecas/{partId}")).Content.ReadFromJsonAsync<PartDto>();
        Assert.Equal(Enumerable.Range(0, Part.MaxPhotos), part!.Photos.Select(p => p.Position).Order());
    }

    [Fact]
    public async Task Ativar_exige_foto_e_a_ultima_foto_de_peca_ativa_nao_sai()
    {
        var (tenant, token, partId) = await PartAsync();
        var client = api.PanelClient();

        var refused = await SendAsync(client, token, HttpMethod.Put, $"/api/painel/pecas/{partId}/situacao", new { status = "active" });
        Assert.Equal("Envie ao menos uma foto antes de ativar a peça.", await TitleAsync(refused));

        var photoId = await UploadOkAsync(token, partId, TestImages.Jpeg(400, 300));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, token, HttpMethod.Put, $"/api/painel/pecas/{partId}/situacao", new { status = "active" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, token, HttpMethod.Delete, $"/api/painel/pecas/{partId}/fotos/{photoId}")).StatusCode);

        await SendAsync(client, token, HttpMethod.Put, $"/api/painel/pecas/{partId}/situacao", new { status = "inactive" });
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, token, HttpMethod.Delete, $"/api/painel/pecas/{partId}/fotos/{photoId}")).StatusCode);
        Assert.False(Storage.Contains(StorageArea.Private, PartPhoto.OriginalKey(tenant.Id, partId, photoId)));
        Assert.All(PartPhoto.Sizes, size => Assert.False(Storage.Contains(StorageArea.Public, PartPhoto.PublicKey(tenant.Id, partId, photoId, size))));
    }

    [Fact]
    public async Task Reordenar_define_a_capa()
    {
        var (_, token, partId) = await PartAsync();
        var a = await UploadOkAsync(token, partId, TestImages.Jpeg(400, 300));
        var b = await UploadOkAsync(token, partId, TestImages.Jpeg(400, 300));

        var reordered = await SendAsync(api.PanelClient(), token, HttpMethod.Put, $"/api/painel/pecas/{partId}/fotos/ordem", new { photoIds = new[] { b, a } });
        Assert.Equal([b, a], (await reordered.Content.ReadFromJsonAsync<PartDto>())!.Photos.OrderBy(p => p.Position).Select(p => p.Id));

        var invalid = await SendAsync(api.PanelClient(), token, HttpMethod.Put, $"/api/painel/pecas/{partId}/fotos/ordem", new { photoIds = new[] { b } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Foto_de_peca_de_outra_loja_nao_e_vista_nem_alterada()
    {
        var (_, tokenA, partA) = await PartAsync();
        var photoA = await UploadOkAsync(tokenA, partA, TestImages.Jpeg(400, 300));
        var (_, tokenB, partB) = await PartAsync();
        var client = api.PanelClient();

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(client, tokenB, $"/api/painel/pecas/{partA}/fotos/{photoA}/300")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(client, tokenB, $"/api/painel/pecas/{partB}/fotos/{photoA}/300")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(client, tokenB, HttpMethod.Delete, $"/api/painel/pecas/{partA}/fotos/{photoA}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(tokenB, partA, TestImages.Jpeg(100, 100))).StatusCode);

        var preview = await GetAsync(client, tokenA, $"/api/painel/pecas/{partA}/fotos/{photoA}/300");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal("image/webp", preview.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Operador_envia_fotos()
    {
        var (_, token, partId) = await PartAsync(TenantRole.Operator);

        await UploadOkAsync(token, partId, TestImages.Jpeg(400, 300));
    }
}
