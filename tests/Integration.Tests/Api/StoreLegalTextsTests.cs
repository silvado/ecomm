using System.Net;
using System.Net.Http.Json;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Store;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RNF04 — política de privacidade e termos por loja: modelo padrão com os dados da loja enquanto ela não escreve os seus;
/// só o Dono edita; vale também com a loja personalizada; cada loja vê só os seus.
/// </summary>
public sealed class StoreLegalTextsTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record EditorDto(string PrivacyPolicy, string Terms, string DefaultPrivacyPolicy, string DefaultTerms);
    private sealed record PublicDto(string PrivacyPolicy, string Terms);

    [Fact]
    public async Task Sem_texto_proprio_a_vitrine_mostra_o_modelo_com_razao_social_e_CNPJ()
    {
        var tenant = await api.CreateTenantAsync();

        var texts = (await api.StoreClient(tenant).GetFromJsonAsync<PublicDto>("/api/loja/privacidade"))!;

        var cnpj = tenant.Cnpj.Value;
        var formatted = $"{cnpj[..2]}.{cnpj[2..5]}.{cnpj[5..8]}/{cnpj[8..12]}-{cnpj[12..]}";
        Assert.Contains($"{tenant.LegalName} (CNPJ {formatted})", texts.PrivacyPolicy, StringComparison.Ordinal);
        Assert.Contains("Lei 13.709/2018", texts.PrivacyPolicy, StringComparison.Ordinal);
        Assert.Contains(tenant.LegalName, texts.Terms, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dono_escreve_os_textos_vazio_volta_ao_modelo_e_outra_loja_nao_ve()
    {
        var tenant = await api.CreateTenantAsync();
        var other = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);

        var saved = await SendAsync(api.PanelClient(), owner, HttpMethod.Put, "/api/painel/loja/privacidade",
            new { politica = "  Nossa política.\r\nLinha 2  ", termos = "Nossos termos." });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var editor = (await saved.Content.ReadFromJsonAsync<EditorDto>())!;
        Assert.Equal(("Nossa política.\nLinha 2", "Nossos termos."), (editor.PrivacyPolicy, editor.Terms));
        Assert.Contains(tenant.LegalName, editor.DefaultPrivacyPolicy, StringComparison.Ordinal);

        var published = (await api.StoreClient(tenant).GetFromJsonAsync<PublicDto>("/api/loja/privacidade"))!;
        Assert.Equal(("Nossa política.\nLinha 2", "Nossos termos."), (published.PrivacyPolicy, published.Terms));
        var otherTexts = (await api.StoreClient(other).GetFromJsonAsync<PublicDto>("/api/loja/privacidade"))!;
        Assert.Contains(other.LegalName, otherTexts.PrivacyPolicy, StringComparison.Ordinal);

        await SendAsync(api.PanelClient(), owner, HttpMethod.Put, "/api/painel/loja/privacidade", new { politica = "", termos = "Nossos termos." });
        published = (await api.StoreClient(tenant).GetFromJsonAsync<PublicDto>("/api/loja/privacidade"))!;
        Assert.Contains("Lei 13.709/2018", published.PrivacyPolicy, StringComparison.Ordinal);
        Assert.Equal("Nossos termos.", published.Terms);
    }

    [Fact]
    public async Task Operador_nao_edita_e_texto_longo_demais_e_recusado()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var operador = await api.SessionForAsync(tenant, TenantRole.Operator);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(api.PanelClient(), operador, HttpMethod.Put, "/api/painel/loja/privacidade", new { politica = "x", termos = "y" })).StatusCode);
        var tooLong = await SendAsync(api.PanelClient(), owner, HttpMethod.Put, "/api/painel/loja/privacidade",
            new { politica = new string('a', StoreBranding.LegalTextMaxLength + 1), termos = "" });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal($"Texto acima de {StoreBranding.LegalTextMaxLength} caracteres.", await TitleAsync(tooLong));
    }

    [Fact]
    public async Task Com_a_loja_personalizada_o_Dono_continua_editando_os_textos_legais()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        await api.WithPlatformAsync(async db =>
        {
            var row = await db.Tenants.FindAsync(tenant.Id);
            row!.SetCustomStorefront(true, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        });

        var saved = await SendAsync(api.PanelClient(), owner, HttpMethod.Put, "/api/painel/loja/privacidade", new { politica = "Política.", termos = "Termos." });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
    }
}
