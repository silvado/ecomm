using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Ecommerce.Infrastructure.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>RF06 — cofre no painel: certificado A1 (só entra), metadados sem valores e aviso de vencimento (CA5).</summary>
public sealed class VaultPanelTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record CertificateDto(string Holder, string? Cnpj, DateTimeOffset ExpiresAt, int DaysLeft);
    private sealed record AlertDto(DateTimeOffset ExpiresAt, int DaysLeft, string Level);
    private sealed record SecretDto(string Kind, string Name, string Hint, DateTimeOffset? ExpiresAt);
    private sealed record ProblemDto(string Title);

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>Certificado de teste no formato do e-CNPJ ICP-Brasil ("RAZAO:CNPJ"), com senha gerada.</summary>
    private static (byte[] Pfx, string Password) Certificate(string cnpj, TimeSpan validFor, bool withKey = true)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN=LOJA TESTE LTDA:{cnpj}, O=ICP-Brasil", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddDays(-400), now + validFor);
        var password = "pfx-" + Guid.NewGuid().ToString("N");
        return withKey
            ? (certificate.Export(X509ContentType.Pkcs12, password), password)
            : (X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert)).Export(X509ContentType.Pkcs12, password), password);
    }

    private static Task<HttpResponseMessage> Upload(HttpClient client, string token, byte[] pfx, string password) =>
        SendAsync(client, token, HttpMethod.Put, "/api/painel/cofre/certificado", new { pfxBase64 = Convert.ToBase64String(pfx), password });

    [Fact]
    public async Task Dono_envia_o_a1_ve_so_os_dados_publicos_e_recebe_aviso_perto_do_vencimento()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        Assert.Equal(HttpStatusCode.NoContent, (await GetAsync(api.PanelClient(), owner, "/api/painel/cofre/certificado")).StatusCode);

        var (pfx, password) = Certificate(tenant.Cnpj.Value, TimeSpan.FromDays(10.5));
        var saved = await Upload(api.PanelClient(), owner, pfx, password);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var view = (await saved.Content.ReadFromJsonAsync<CertificateDto>())!;
        Assert.Equal(("LOJA TESTE LTDA", tenant.Cnpj.Value, 10), (view.Holder, view.Cnpj, view.DaysLeft));

        var read = (await (await GetAsync(api.PanelClient(), owner, "/api/painel/cofre/certificado")).Content.ReadFromJsonAsync<CertificateDto>())!;
        Assert.Equal(view.ExpiresAt, read.ExpiresAt);

        var alert = (await (await GetAsync(api.PanelClient(), owner, "/api/painel/cofre/alerta")).Content.ReadFromJsonAsync<AlertDto>())!;
        Assert.Equal(("warning", 10), (alert.Level, alert.DaysLeft));

        // Metadados: o A1 e a senha aparecem sem dica e com a validade; nunca o conteúdo.
        var list = await (await GetAsync(api.PanelClient(), owner, "/api/painel/cofre")).Content.ReadAsStringAsync();
        var secrets = System.Text.Json.JsonSerializer.Deserialize<List<SecretDto>>(list, Json)!;
        Assert.Equal(["a1Certificate", "a1CertificatePassword"], secrets.Select(s => s.Kind).Order());
        Assert.All(secrets, s => Assert.Equal("", s.Hint));
        Assert.DoesNotContain(password, list, StringComparison.Ordinal);
        Assert.DoesNotContain(password, api.Logs.AllText, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(api.PanelClient(), owner, HttpMethod.Delete, "/api/painel/cofre/certificado")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await GetAsync(api.PanelClient(), owner, "/api/painel/cofre/alerta")).StatusCode);
    }

    [Fact]
    public async Task Senha_errada_vencido_outro_cnpj_ou_sem_chave_sao_recusados_sem_gravar()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var (pfx, password) = Certificate(tenant.Cnpj.Value, TimeSpan.FromDays(200));

        async Task<string> Refused(byte[] file, string pwd)
        {
            var response = await Upload(api.PanelClient(), owner, file, pwd);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<ProblemDto>())!.Title;
        }

        var wrong = await Refused(pfx, password + "x");
        Assert.Contains("senha", wrong, StringComparison.Ordinal);
        Assert.DoesNotContain(password, wrong, StringComparison.Ordinal);

        var (expiredPfx, expiredPassword) = Certificate(tenant.Cnpj.Value, TimeSpan.FromDays(-1));
        Assert.Contains("venceu", await Refused(expiredPfx, expiredPassword), StringComparison.Ordinal);
        var (otherPfx, otherPassword) = Certificate(Cnpjs.Random().Value, TimeSpan.FromDays(200));
        Assert.Contains("não do CNPJ da loja", await Refused(otherPfx, otherPassword), StringComparison.Ordinal);
        var (noKeyPfx, noKeyPassword) = Certificate(tenant.Cnpj.Value, TimeSpan.FromDays(200), withKey: false);
        Assert.Contains("chave privada", await Refused(noKeyPfx, noKeyPassword), StringComparison.Ordinal);
        Assert.Contains("Envie o arquivo", await Refused([], password), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NoContent, (await GetAsync(api.PanelClient(), owner, "/api/painel/cofre/certificado")).StatusCode);
    }

    [Fact]
    public async Task Operador_nao_acessa_o_cofre()
    {
        var tenant = await api.CreateTenantAsync();
        var operatorToken = await api.SessionForAsync(tenant, TenantRole.Operator);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(api.PanelClient(), operatorToken, "/api/painel/cofre/alerta")).StatusCode);
        var (pfx, password) = Certificate(tenant.Cnpj.Value, TimeSpan.FromDays(100));
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload(api.PanelClient(), operatorToken, pfx, password)).StatusCode);
    }

    [Fact]
    public async Task Monitor_registra_aviso_nos_dias_30_15_5_e_no_vencimento()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var (pfx, password) = Certificate(tenant.Cnpj.Value, TimeSpan.FromDays(200));
        await Upload(api.PanelClient(), owner, pfx, password);
        var monitor = ActivatorUtilities.CreateInstance<CertificateExpiryMonitor>(api.Services);

        async Task SetExpiry(double days)
        {
            await using var scope = api.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenant.Id);
            var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
            var at = DateTimeOffset.UtcNow.AddDays(days);
            await db.Database.ExecuteSqlAsync($"UPDATE tenant_secrets SET expires_at = {at} WHERE kind = 'A1Certificate'");
        }

        // O que vai para o log é o que o monitor devolve (o log em si é compartilhado entre os testes em paralelo).
        async Task<int?> ReminderFor()
        {
            var reminders = await monitor.CheckAsync(CancellationToken.None);
            return reminders.Where(r => r.TenantId == tenant.Id).Select(r => (int?)r.DaysLeft).SingleOrDefault();
        }

        await SetExpiry(15.5);
        Assert.Equal(15, await ReminderFor());
        await SetExpiry(30.5);
        Assert.Equal(30, await ReminderFor());
        await SetExpiry(-0.5);
        Assert.Equal(-1, await ReminderFor());
        await SetExpiry(20.5); // fora dos dias de aviso
        Assert.Null(await ReminderFor());
    }
}
