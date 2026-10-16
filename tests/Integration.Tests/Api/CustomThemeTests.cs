using System.Net;
using System.Net.Http.Json;
using Ecommerce.Domain.Platform;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF40 CA1 — tema e páginas exclusivos da loja personalizada: só o superadmin grava, tudo limpo no servidor e auditado;
/// a vitrine só aplica com a opção ligada; o painel da loja não alcança nada disso.
/// </summary>
public sealed class CustomThemeTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record PageDto(Guid Id, string Slug, string Title, string Html, bool Published, int Position);
    private sealed record ThemeDto(Guid TenantId, bool CustomStorefront, string Css, string HeaderHtml, string FooterHtml, string? UpdatedBy, List<PageDto> Pages);
    private sealed record SavedDto(ThemeDto Theme, List<string> Removed);
    private sealed record PreviewDto(string Css, string HeaderHtml, string FooterHtml, List<string> Removed);
    private sealed record LinkDto(string Slug, string Title);
    private sealed record CustomDto(string Css, string HeaderHtml, string FooterHtml, List<LinkDto> Pages);
    private sealed record StoreDto(string Name, CustomDto? Custom);
    private sealed record PublicPageDto(string Slug, string Title, string Html);
    private sealed record AuditItemDto(string Action, string? Details);
    private sealed record AuditPageDto(List<AuditItemDto> Items);

    private static readonly object Theme = new
    {
        css = "h1 { color: #b00020; } .banner { behavior: url(x.htc); padding: 1rem; }",
        cabecalho = "<div class=\"banner\"><script>alert(1)</script><a href=\"javascript:alert(1)\">Promoção</a></div>",
        rodape = "<p onclick=\"alert(1)\">Desde 1990</p>",
    };

    [Fact]
    public async Task Superadmin_salva_tema_limpo_e_a_vitrine_so_aplica_com_a_loja_personalizada_ligada()
    {
        var client = api.PanelClient();
        var admin = await PlatformAdminTests.EnrolledAdminAsync(api, client);
        var tenant = await api.CreateTenantAsync();

        var saved = await SendAsync(client, admin.Token, HttpMethod.Put, $"/api/plataforma/lojas/{tenant.Id}/tema", Theme);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var result = (await saved.Content.ReadFromJsonAsync<SavedDto>())!;
        Assert.DoesNotContain("script", result.Theme.HeaderHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript", result.Theme.HeaderHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", result.Theme.FooterHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("behavior", result.Theme.Css, StringComparison.Ordinal);
        Assert.Contains("Desde 1990", result.Theme.FooterHtml, StringComparison.Ordinal);
        Assert.Contains("Cabeçalho: tag <script>", result.Removed);
        Assert.Contains("CSS: propriedade behavior", result.Removed);
        Assert.Equal(admin.Email, result.Theme.UpdatedBy);

        // Desligada: a vitrine continua com o visual padrão.
        var store = (await api.StoreClient(tenant).GetFromJsonAsync<StoreDto>("/api/loja/identidade"))!;
        Assert.Null(store.Custom);

        await SendAsync(client, admin.Token, HttpMethod.Put, $"/api/plataforma/lojas/{tenant.Id}/loja-personalizada", new { ligada = true });
        store = (await api.StoreClient(tenant).GetFromJsonAsync<StoreDto>("/api/loja/identidade"))!;
        Assert.NotNull(store.Custom);
        Assert.StartsWith(".custom-theme {", store.Custom.Css, StringComparison.Ordinal);
        Assert.Contains("padding", store.Custom.Css, StringComparison.Ordinal);
        Assert.Equal(result.Theme.HeaderHtml, store.Custom.HeaderHtml);

        // Mudança no tema aparece na hora (cache limpo ao salvar).
        await SendAsync(client, admin.Token, HttpMethod.Put, $"/api/plataforma/lojas/{tenant.Id}/tema", new { css = "", cabecalho = "<p>Novo</p>", rodape = "" });
        store = (await api.StoreClient(tenant).GetFromJsonAsync<StoreDto>("/api/loja/identidade"))!;
        Assert.Equal(("", "<p>Novo</p>"), (store.Custom!.Css, store.Custom.HeaderHtml));

        await SendAsync(client, admin.Token, HttpMethod.Put, $"/api/plataforma/lojas/{tenant.Id}/loja-personalizada", new { ligada = false });
        store = (await api.StoreClient(tenant).GetFromJsonAsync<StoreDto>("/api/loja/identidade"))!;
        Assert.Null(store.Custom);

        var audit = (await (await GetAsync(client, admin.Token, $"/api/plataforma/auditoria?loja={tenant.Id}&acao={PlatformAuditActions.ThemeUpdated}")).Content.ReadFromJsonAsync<AuditPageDto>())!;
        Assert.Equal(2, audit.Items.Count);
        Assert.Contains("removido", audit.Items[1].Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Previa_limpa_sem_gravar()
    {
        var client = api.PanelClient();
        var admin = await PlatformAdminTests.EnrolledAdminAsync(api, client);
        var tenant = await api.CreateTenantAsync();

        var preview = await PostAsync(client, admin.Token, $"/api/plataforma/lojas/{tenant.Id}/tema/previa", Theme);
        var cleaned = (await preview.Content.ReadFromJsonAsync<PreviewDto>())!;
        Assert.DoesNotContain("script", cleaned.HeaderHtml, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(cleaned.Removed);

        var theme = (await (await GetAsync(client, admin.Token, $"/api/plataforma/lojas/{tenant.Id}/tema")).Content.ReadFromJsonAsync<ThemeDto>())!;
        Assert.Equal(("", "", null), (theme.HeaderHtml, theme.Css, theme.UpdatedBy));
    }

    [Fact]
    public async Task Paginas_publicadas_aparecem_na_vitrine_e_rascunho_nao()
    {
        var client = api.PanelClient();
        var admin = await PlatformAdminTests.EnrolledAdminAsync(api, client);
        var tenant = await api.CreateTenantAsync();
        var other = await api.CreateTenantAsync();
        await SendAsync(client, admin.Token, HttpMethod.Put, $"/api/plataforma/lojas/{tenant.Id}/loja-personalizada", new { ligada = true });
        await SendAsync(client, admin.Token, HttpMethod.Put, $"/api/plataforma/lojas/{other.Id}/loja-personalizada", new { ligada = true });

        var created = await PostAsync(client, admin.Token, $"/api/plataforma/lojas/{tenant.Id}/paginas",
            new { endereco = "Quem-Somos", titulo = "Quem somos", conteudo = "<h2>Nossa história</h2><img src=x onerror=alert(1)>", publicada = true, ordem = 2 });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var page = Assert.Single((await created.Content.ReadFromJsonAsync<SavedDto>())!.Theme.Pages);
        Assert.Equal("quem-somos", page.Slug);
        Assert.DoesNotContain("onerror", page.Html, StringComparison.Ordinal);
        var draft = await PostAsync(client, admin.Token, $"/api/plataforma/lojas/{tenant.Id}/paginas",
            new { endereco = "promocoes", titulo = "Promoções", conteudo = "<p>Em breve</p>", publicada = false, ordem = 1 });
        var draftId = (await draft.Content.ReadFromJsonAsync<SavedDto>())!.Theme.Pages.Single(p => p.Slug == "promocoes").Id;

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, admin.Token, $"/api/plataforma/lojas/{tenant.Id}/paginas",
            new { endereco = "quem-somos", titulo = "Outra", conteudo = "", publicada = true, ordem = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, admin.Token, $"/api/plataforma/lojas/{tenant.Id}/paginas",
            new { endereco = "../admin", titulo = "Ruim", conteudo = "", publicada = true, ordem = 0 })).StatusCode);

        var store = (await api.StoreClient(tenant).GetFromJsonAsync<StoreDto>("/api/loja/identidade"))!;
        Assert.Equal([new LinkDto("quem-somos", "Quem somos")], store.Custom!.Pages);
        var shown = (await api.StoreClient(tenant).GetFromJsonAsync<PublicPageDto>("/api/loja/paginas/quem-somos"))!;
        Assert.Equal("Quem somos", shown.Title);
        Assert.Equal(HttpStatusCode.NotFound, (await api.StoreClient(tenant).GetAsync("/api/loja/paginas/promocoes")).StatusCode);
        // Isolamento: a página de uma loja não abre no endereço de outra.
        Assert.Equal(HttpStatusCode.NotFound, (await api.StoreClient(other).GetAsync("/api/loja/paginas/quem-somos")).StatusCode);

        var published = await SendAsync(client, admin.Token, HttpMethod.Put, $"/api/plataforma/lojas/{tenant.Id}/paginas/{draftId}",
            new { endereco = "promocoes", titulo = "Promoções", conteudo = "<p>Agora sim</p>", publicada = true, ordem = 1 });
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        store = (await api.StoreClient(tenant).GetFromJsonAsync<StoreDto>("/api/loja/identidade"))!;
        Assert.Equal(["promocoes", "quem-somos"], store.Custom!.Pages.Select(p => p.Slug).ToList());

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, admin.Token, HttpMethod.Delete, $"/api/plataforma/lojas/{tenant.Id}/paginas/{draftId}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.StoreClient(tenant).GetAsync("/api/loja/paginas/promocoes")).StatusCode);
        // Página de uma loja não é alterada pelo endereço de outra.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(client, admin.Token, HttpMethod.Delete, $"/api/plataforma/lojas/{other.Id}/paginas/{page.Id}", null)).StatusCode);

        // Desligada, nenhuma página abre.
        await SendAsync(client, admin.Token, HttpMethod.Put, $"/api/plataforma/lojas/{tenant.Id}/loja-personalizada", new { ligada = false });
        Assert.Equal(HttpStatusCode.NotFound, (await api.StoreClient(tenant).GetAsync("/api/loja/paginas/quem-somos")).StatusCode);
    }

    [Fact]
    public async Task Painel_da_loja_nao_alcanca_o_tema()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);

        var response = await SendAsync(api.PanelClient(), owner, HttpMethod.Put, $"/api/plataforma/lojas/{tenant.Id}/tema", Theme);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string token, string path, object body) =>
        SendAsync(client, token, HttpMethod.Post, path, body);
}
