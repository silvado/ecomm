using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Ecommerce.Application.Storage;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Store;
using Ecommerce.Infrastructure.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>RF01 CA2 — logo da loja: envio, validação pelo conteúdo, troca, remoção e isolamento.</summary>
public sealed class StoreLogoTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record ProfileDto(Guid? LogoId);
    private sealed record PublicDto(string Name, string? LogoUrl);

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 1, 2, 3, 4];

    private InMemoryFileStorage Storage => api.Services.GetRequiredService<InMemoryFileStorage>();

    private async Task<HttpResponseMessage> UploadAsync(string token, byte[] bytes, string declaredType = "image/png")
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/painel/loja/logo") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(declaredType);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await api.PanelClient().SendAsync(request);
    }

    private async Task<(Ecommerce.Domain.Platform.Tenant Tenant, string Owner)> StoreAsync()
    {
        var tenant = await api.CreateTenantAsync();
        return (tenant, await api.SessionForAsync(tenant));
    }

    private Task<PublicDto?> PublicAsync(Ecommerce.Domain.Platform.Tenant tenant) =>
        api.ClientFor($"{tenant.Slug}.plataforma.test").GetFromJsonAsync<PublicDto>("/api/loja/identidade");

    [Fact]
    public async Task Logo_enviado_aparece_na_loja_com_cache_longo_e_cabecalhos_de_seguranca()
    {
        var (tenant, owner) = await StoreAsync();

        var uploaded = await UploadAsync(owner, Png);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        var logoId = (await uploaded.Content.ReadFromJsonAsync<ProfileDto>())!.LogoId!.Value;

        var store = await PublicAsync(tenant);
        Assert.Equal($"/api/loja/logo/{logoId}", store!.LogoUrl);

        var logo = await api.ClientFor($"{tenant.Slug}.plataforma.test").GetAsync(store.LogoUrl);
        Assert.Equal(HttpStatusCode.OK, logo.StatusCode);
        Assert.Equal(Png, await logo.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", logo.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", logo.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("sandbox", logo.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.True(logo.Headers.CacheControl!.Public);
        Assert.Equal(TimeSpan.FromDays(365), logo.Headers.CacheControl.MaxAge);
    }

    [Fact]
    public async Task Tipo_vem_do_conteudo_e_nao_do_cabecalho_enviado()
    {
        var (_, owner) = await StoreAsync();
        var html = Encoding.UTF8.GetBytes("<html><script>alert(document.cookie)</script></html>");

        var refused = await UploadAsync(owner, html, declaredType: "image/png");

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("Formato não aceito: envie PNG, JPG ou WebP.", await TitleAsync(refused));
    }

    [Fact]
    public async Task Arquivo_acima_de_2_MB_ou_vazio_e_recusado()
    {
        var (_, owner) = await StoreAsync();
        var big = new byte[LogoImage.MaxBytes + 1];
        Png.CopyTo(big, 0);
        var exact = new byte[LogoImage.MaxBytes];
        Png.CopyTo(exact, 0);

        Assert.Equal("O logo pode ter no máximo 2 MB.", await TitleAsync(await UploadAsync(owner, big)));
        Assert.Equal("Envie a imagem do logo.", await TitleAsync(await UploadAsync(owner, [])));
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(owner, exact)).StatusCode);
    }

    [Fact]
    public async Task Trocar_o_logo_apaga_o_anterior_e_a_url_antiga_some()
    {
        var (tenant, owner) = await StoreAsync();
        var first = (await (await UploadAsync(owner, Png)).Content.ReadFromJsonAsync<ProfileDto>())!.LogoId!.Value;

        var second = (await (await UploadAsync(owner, Png)).Content.ReadFromJsonAsync<ProfileDto>())!.LogoId!.Value;

        Assert.NotEqual(first, second);
        Assert.False(Storage.Contains(StorageArea.Public, LogoImage.StorageKey(tenant.Id, first)));
        Assert.True(Storage.Contains(StorageArea.Public, LogoImage.StorageKey(tenant.Id, second)));
        var store = api.ClientFor($"{tenant.Slug}.plataforma.test");
        Assert.Equal(HttpStatusCode.NotFound, (await store.GetAsync($"/api/loja/logo/{first}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await store.GetAsync($"/api/loja/logo/{second}")).StatusCode);
    }

    [Fact]
    public async Task Remover_o_logo_volta_ao_nome_e_apaga_o_arquivo()
    {
        var (tenant, owner) = await StoreAsync();
        var logoId = (await (await UploadAsync(owner, Png)).Content.ReadFromJsonAsync<ProfileDto>())!.LogoId!.Value;

        var removed = await SendAsync(api.PanelClient(), owner, HttpMethod.Delete, "/api/painel/loja/logo");

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Null((await PublicAsync(tenant))!.LogoUrl);
        Assert.False(Storage.Contains(StorageArea.Public, LogoImage.StorageKey(tenant.Id, logoId)));
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(api.PanelClient(), owner, "/api/painel/loja/logo")).StatusCode);
    }

    [Fact]
    public async Task Logo_de_uma_loja_nao_e_servido_no_dominio_de_outra()
    {
        var (a, ownerA) = await StoreAsync();
        var (b, _) = await StoreAsync();
        var logoA = (await (await UploadAsync(ownerA, Png)).Content.ReadFromJsonAsync<ProfileDto>())!.LogoId!.Value;

        var fromB = await api.ClientFor($"{b.Slug}.plataforma.test").GetAsync($"/api/loja/logo/{logoA}");

        Assert.Equal(HttpStatusCode.NotFound, fromB.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.ClientFor($"{a.Slug}.plataforma.test").GetAsync($"/api/loja/logo/{logoA}")).StatusCode);
    }

    [Fact]
    public async Task Previa_do_painel_e_sem_cache_e_operador_nao_envia()
    {
        var (tenant, owner) = await StoreAsync();
        await UploadAsync(owner, Png);
        var op = await api.SessionForAsync(tenant, TenantRole.Operator);

        var preview = await GetAsync(api.PanelClient(), owner, "/api/painel/loja/logo");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.True(preview.Headers.CacheControl!.NoStore);
        Assert.Equal(Png, await preview.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(op, Png)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(api.PanelClient(), op, HttpMethod.Delete, "/api/painel/loja/logo")).StatusCode);
    }
}
