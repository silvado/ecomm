using System.Net;
using System.Net.Http.Json;
using Ecommerce.Api.Platform;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF07 CA3 / ADR-0007 — superadmin: conta separada, MFA (TOTP) obrigatório, token que não abre o painel de loja (e
/// vice-versa), sessão derrubada na hora, ações sobre lojas e auditoria somente inclusão.
/// </summary>
public sealed class PlatformAdminTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private const string NewPassword = "senha-nova-da-plataforma-1";

    private sealed record AdminDto(string Email, bool MustChangePassword, bool HasMfa);
    private sealed record StepDto(string Stage, string AccessToken, AdminDto Admin, List<string>? RecoveryCodes);
    private sealed record EnrollmentDto(string Secret, string ProvisioningUri);
    private sealed record TenantItemDto(Guid Id, string Slug, string Status, string PlanCode, bool CustomStorefront);
    private sealed record TenantDetailDto(Guid Id, string Slug, string Status, string PlanCode, DateTimeOffset? CustomStorefrontSince, List<UserDto> Users);
    private sealed record UserDto(string Email, string Role);
    private sealed record CreatedDto(Guid Id, string Slug, string Host, string OwnerEmail, string? TemporaryPassword);
    private sealed record AuditItemDto(string AdminEmail, string Action, Guid? TenantId, string? TenantSlug, string? Details, string? Ip);
    private sealed record AuditPageDto(List<AuditItemDto> Items, bool HasMore);
    private sealed record PlanDto(string Code, Dictionary<string, int> Limits, List<string> Features);

    /// <summary>Superadmin pronto: senha trocada, MFA cadastrado, sessão aberta.</summary>
    private sealed record Admin(string Email, byte[] Secret, string Token, string Cookie, List<string> RecoveryCodes);

    private async Task<(string Email, string Password)> CreateAdminAsync()
    {
        await using var scope = api.Services.CreateAsyncScope();
        var provisioning = ActivatorUtilities.CreateInstance<PlatformAdminProvisioning>(scope.ServiceProvider);
        var email = $"sa{Guid.NewGuid().ToString("N")[..8]}@plataforma.test";
        var created = await provisioning.CreateAsync(email);
        return (email, created!.Value.TemporaryPassword);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object? body, string? token = null) =>
        token is null ? await client.PostAsJsonAsync(path, body) : await SendAsync(client, token, HttpMethod.Post, path, body);

    private static async Task<StepDto> StepAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<StepDto>())!;
    }

    private static string Cookie(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(PlatformAuthEndpoints.SessionCookie + "=", StringComparison.Ordinal))
            .Split(';')[0][(PlatformAuthEndpoints.SessionCookie.Length + 1)..];

    private static string Code(byte[] secret, int stepsAhead = 0) =>
        Totp.Compute(secret, Totp.StepAt(DateTimeOffset.UtcNow) + stepsAhead);

    /// <summary>Primeiro acesso completo: senha provisória → senha nova → app autenticador → sessão.</summary>
    private async Task<Admin> EnrolledAdminAsync(HttpClient client)
    {
        var (email, temporary) = await CreateAdminAsync();
        var setup = await StepAsync(await PostAsync(client, "/api/plataforma/auth/entrar", new { email, password = temporary }));
        setup = await StepAsync(await SendAsync(client, setup.AccessToken, HttpMethod.Put, "/api/plataforma/auth/senha",
            new { currentPassword = temporary, newPassword = NewPassword }));
        var enrollment = (await (await PostAsync(client, "/api/plataforma/auth/mfa", null, setup.AccessToken)).Content.ReadFromJsonAsync<EnrollmentDto>())!;
        var secret = Base32Decode(enrollment.Secret);
        var confirmed = await PostAsync(client, "/api/plataforma/auth/mfa/confirmar", new { code = Code(secret) }, setup.AccessToken);
        var full = await StepAsync(confirmed);
        return new Admin(email, secret, full.AccessToken, Cookie(confirmed), full.RecoveryCodes!);
    }

    [Fact]
    public async Task Primeiro_acesso_exige_trocar_a_senha_e_cadastrar_o_MFA_antes_de_abrir_o_painel()
    {
        var client = api.PanelClient();
        var (email, temporary) = await CreateAdminAsync();

        var setup = await StepAsync(await PostAsync(client, "/api/plataforma/auth/entrar", new { email, password = temporary }));
        Assert.Equal((PlatformStages.Setup, true, false), (setup.Stage, setup.Admin.MustChangePassword, setup.Admin.HasMfa));
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(client, setup.AccessToken, "/api/plataforma/lojas")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(client, "/api/plataforma/auth/mfa", null, setup.AccessToken)).StatusCode);

        setup = await StepAsync(await SendAsync(client, setup.AccessToken, HttpMethod.Put, "/api/plataforma/auth/senha",
            new { currentPassword = temporary, newPassword = NewPassword }));
        var enrollment = (await (await PostAsync(client, "/api/plataforma/auth/mfa", null, setup.AccessToken)).Content.ReadFromJsonAsync<EnrollmentDto>())!;
        Assert.StartsWith($"otpauth://totp/Ecomm%20plataforma:{Uri.EscapeDataString(email)}?secret={enrollment.Secret}", enrollment.ProvisioningUri, StringComparison.Ordinal);
        var secret = Base32Decode(enrollment.Secret);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await PostAsync(client, "/api/plataforma/auth/mfa/confirmar", new { code = Code(secret, stepsAhead: 5) }, setup.AccessToken)).StatusCode);
        var confirmed = await PostAsync(client, "/api/plataforma/auth/mfa/confirmar", new { code = Code(secret) }, setup.AccessToken);
        var full = await StepAsync(confirmed);

        Assert.Equal(PlatformStages.Full, full.Stage);
        Assert.Equal(PlatformAdminRecoveryCode.CountPerAdmin, full.RecoveryCodes!.Distinct().Count());
        Assert.Contains("HttpOnly", confirmed.Headers.GetValues("Set-Cookie").Single(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, full.AccessToken, "/api/plataforma/eu")).StatusCode);
        // MFA ativo não recomeça com o token de cadastro antigo.
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(client, "/api/plataforma/auth/mfa", null, setup.AccessToken)).StatusCode);

        var stored = await api.WithPlatformAsync(db => db.PlatformAdmins.AsNoTracking().SingleAsync(a => a.Email == email));
        Assert.DoesNotContain(enrollment.Secret, Convert.ToBase64String(stored.TotpSecretCipher!), StringComparison.Ordinal);
        Assert.NotEqual(secret, stored.TotpSecretCipher);
    }

    [Fact]
    public async Task Login_com_MFA_pede_o_codigo_e_o_mesmo_codigo_nao_vale_duas_vezes()
    {
        var client = api.PanelClient();
        var admin = await EnrolledAdminAsync(client);

        var step = await StepAsync(await PostAsync(client, "/api/plataforma/auth/entrar", new { email = admin.Email, password = NewPassword }));
        Assert.Equal(PlatformStages.Code, step.Stage);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(client, step.AccessToken, "/api/plataforma/lojas")).StatusCode);

        // O cadastro já usou o passo atual: o próximo (tolerância de 1 passo) vale uma vez.
        var code = Code(admin.Secret, stepsAhead: 1);
        var full = await StepAsync(await PostAsync(client, "/api/plataforma/auth/codigo", new { code }, step.AccessToken));
        Assert.Equal(PlatformStages.Full, full.Stage);

        var again = await StepAsync(await PostAsync(client, "/api/plataforma/auth/entrar", new { email = admin.Email, password = NewPassword }));
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(client, "/api/plataforma/auth/codigo", new { code }, again.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task Codigo_de_recuperacao_vale_uma_vez_e_cinco_erros_bloqueiam_ate_o_codigo_certo()
    {
        var admin = await EnrolledAdminAsync(api.PanelClient());
        // O limite de login é por IP (10/min): cada tentativa sai de um IP diferente para testar só o bloqueio por conta.
        HttpClient NewClient() => api.PanelClient();
        async Task<string> CodeStepAsync() =>
            (await StepAsync(await PostAsync(NewClient(), "/api/plataforma/auth/entrar", new { email = admin.Email, password = NewPassword }))).AccessToken;

        var recovery = admin.RecoveryCodes[0].ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(NewClient(), "/api/plataforma/auth/codigo", new { code = recovery }, await CodeStepAsync())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(NewClient(), "/api/plataforma/auth/codigo", new { code = admin.RecoveryCodes[0] }, await CodeStepAsync())).StatusCode);

        var token = await CodeStepAsync();
        for (var i = 0; i < 4; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(NewClient(), "/api/plataforma/auth/codigo", new { code = "000000" }, token)).StatusCode);
        // 1 erro acima + 4 = 5: bloqueado, nem o código de recuperação certo passa.
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(NewClient(), "/api/plataforma/auth/codigo", new { code = admin.RecoveryCodes[1] }, token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(NewClient(), "/api/plataforma/auth/entrar", new { email = admin.Email, password = NewPassword })).StatusCode);
    }

    [Fact]
    public async Task Token_de_plataforma_nao_abre_o_painel_de_loja_e_vice_versa()
    {
        var client = api.PanelClient();
        var admin = await EnrolledAdminAsync(client);
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, admin.Token, "/api/painel/eu")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, owner, "/api/plataforma/lojas")).StatusCode);
        // E a conta da plataforma não entra pelo login das lojas.
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, admin.Email, NewPassword)).Response.StatusCode);
    }

    [Fact]
    public async Task Sair_derruba_o_token_na_hora_e_renovar_so_funciona_com_sessao_aberta()
    {
        var client = api.PanelClient();
        var admin = await EnrolledAdminAsync(client);

        using (var renew = new HttpRequestMessage(HttpMethod.Post, "/api/plataforma/auth/renovar"))
        {
            renew.Headers.Add("Cookie", $"{PlatformAuthEndpoints.SessionCookie}={admin.Cookie}");
            Assert.Equal(PlatformStages.Full, (await StepAsync(await client.SendAsync(renew))).Stage);
        }

        using (var logout = new HttpRequestMessage(HttpMethod.Post, "/api/plataforma/auth/sair"))
        {
            logout.Headers.Add("Cookie", $"{PlatformAuthEndpoints.SessionCookie}={admin.Cookie}");
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(logout)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, admin.Token, "/api/plataforma/eu")).StatusCode);
        using var after = new HttpRequestMessage(HttpMethod.Post, "/api/plataforma/auth/renovar");
        after.Headers.Add("Cookie", $"{PlatformAuthEndpoints.SessionCookie}={admin.Cookie}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(after)).StatusCode);
    }

    [Fact]
    public async Task Superadmin_cria_loja_troca_plano_suspende_reativa_e_tudo_fica_na_auditoria()
    {
        var client = api.PanelClient();
        var admin = await EnrolledAdminAsync(client);
        var slug = "sa" + Guid.NewGuid().ToString("N")[..8];
        var cnpj = Cnpjs.Random().Value;

        var created = await PostAsync(client, "/api/plataforma/lojas",
            new { slug, cnpj, razaoSocial = "Peças SA Ltda", nome = "Peças SA", dono = $"dono@{slug}.test" }, admin.Token);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var shop = (await created.Content.ReadFromJsonAsync<CreatedDto>())!;
        Assert.Equal($"{slug}.plataforma.test", shop.Host);
        Assert.False(string.IsNullOrEmpty(shop.TemporaryPassword));
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, "/api/plataforma/lojas",
            new { slug = slug + "x", cnpj, razaoSocial = "Outra", nome = "Outra", dono = "outra@x.test" }, admin.Token)).StatusCode);

        var found = (await (await GetAsync(client, admin.Token, $"/api/plataforma/lojas?busca={slug.ToUpperInvariant()}")).Content.ReadFromJsonAsync<List<TenantItemDto>>())!;
        Assert.Equal(shop.Id, Assert.Single(found).Id);
        var byCnpj = (await (await GetAsync(client, admin.Token, $"/api/plataforma/lojas?busca={cnpj}")).Content.ReadFromJsonAsync<List<TenantItemDto>>())!;
        Assert.Equal(shop.Id, Assert.Single(byCnpj).Id);

        var detail = (await (await GetAsync(client, admin.Token, $"/api/plataforma/lojas/{shop.Id}")).Content.ReadFromJsonAsync<TenantDetailDto>())!;
        Assert.Equal(("Active", Plan.Essential), (detail.Status, detail.PlanCode));
        Assert.Equal(($"dono@{slug}.test", "owner"), (Assert.Single(detail.Users).Email, detail.Users[0].Role.ToLowerInvariant()));

        var plan = await SendAsync(client, admin.Token, HttpMethod.Put, $"/api/plataforma/lojas/{shop.Id}/plano", new { plano = "profissional" });
        Assert.Equal(Plan.Professional, (await plan.Content.ReadFromJsonAsync<TenantDetailDto>())!.PlanCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(client, admin.Token, HttpMethod.Put, $"/api/plataforma/lojas/{shop.Id}/plano", new { plano = "ouro" })).StatusCode);

        var tenant = await api.WithPlatformAsync(db => db.Tenants.AsNoTracking().SingleAsync(t => t.Id == shop.Id));
        Assert.Equal(HttpStatusCode.OK, (await api.StoreClient(tenant).GetAsync("/api/loja/identidade")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, $"/api/plataforma/lojas/{shop.Id}/suspender", null, admin.Token)).StatusCode);
        // Catálogo invalidado na hora: a vitrine já responde como suspensa.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await api.StoreClient(tenant).GetAsync("/api/loja/identidade")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, $"/api/plataforma/lojas/{shop.Id}/reativar", null, admin.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.StoreClient(tenant).GetAsync("/api/loja/identidade")).StatusCode);

        var custom = await SendAsync(client, admin.Token, HttpMethod.Put, $"/api/plataforma/lojas/{shop.Id}/loja-personalizada", new { ligada = true });
        Assert.NotNull((await custom.Content.ReadFromJsonAsync<TenantDetailDto>())!.CustomStorefrontSince);

        var audit = (await (await GetAsync(client, admin.Token, $"/api/plataforma/auditoria?loja={shop.Id}")).Content.ReadFromJsonAsync<AuditPageDto>())!;
        Assert.Equal(
            [PlatformAuditActions.CustomStorefrontChanged, PlatformAuditActions.TenantReactivated, PlatformAuditActions.TenantSuspended,
             PlatformAuditActions.PlanChanged, PlatformAuditActions.TenantViewed, PlatformAuditActions.TenantCreated],
            audit.Items.Select(i => i.Action).ToList());
        Assert.All(audit.Items, i => Assert.Equal((admin.Email, slug), (i.AdminEmail, i.TenantSlug)));
        Assert.All(audit.Items, i => Assert.False(string.IsNullOrEmpty(i.Ip)));
        Assert.DoesNotContain(audit.Items, i => i.Details?.Contains(shop.TemporaryPassword!, StringComparison.Ordinal) == true);
        Assert.Equal("essencial → profissional", audit.Items.Single(i => i.Action == PlatformAuditActions.PlanChanged).Details);

        var logins = (await (await GetAsync(client, admin.Token, $"/api/plataforma/auditoria?email={admin.Email}&acao={PlatformAuditActions.MfaEnrolled}")).Content.ReadFromJsonAsync<AuditPageDto>())!;
        Assert.Single(logins.Items);
    }

    [Fact]
    public async Task Planos_aparecem_com_limites_e_recursos()
    {
        var admin = await EnrolledAdminAsync(api.PanelClient());

        var plans = (await (await GetAsync(api.PanelClient(), admin.Token, "/api/plataforma/planos")).Content.ReadFromJsonAsync<List<PlanDto>>())!;

        Assert.Equal([Plan.Essential, Plan.Professional, Plan.Complete], plans.Select(p => p.Code).ToList());
        Assert.Equal(500, plans[0].Limits[PlanLimit.ActiveParts]);
        Assert.Contains(PlanFeature.CustomDomain, plans[1].Features);
    }

    [Fact]
    public async Task Auditoria_e_somente_inclusao_para_o_papel_da_aplicacao()
    {
        await CreateAdminAsync();

        var update = await Assert.ThrowsAsync<PostgresException>(() => api.SqlAsync($"UPDATE platform_audit_log SET action = 'apagado'"));
        var delete = await Assert.ThrowsAsync<PostgresException>(() => api.SqlAsync($"DELETE FROM platform_audit_log"));

        Assert.Equal((PostgresErrorCodes.InsufficientPrivilege, PostgresErrorCodes.InsufficientPrivilege), (update.SqlState, delete.SqlState));
    }

    [Fact]
    public async Task Senha_segredo_e_codigos_nao_aparecem_nos_logs()
    {
        var client = api.PanelClient();
        var admin = await EnrolledAdminAsync(client);

        Assert.NotEmpty(api.Logs.AllText);
        Assert.DoesNotContain(NewPassword, api.Logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(Base32.Encode(admin.Secret), api.Logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(admin.Cookie, api.Logs.AllText, StringComparison.Ordinal);
        Assert.All(admin.RecoveryCodes, c => Assert.DoesNotContain(c, api.Logs.AllText, StringComparison.Ordinal));
    }

    private static byte[] Base32Decode(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }
        return [.. output];
    }
}
