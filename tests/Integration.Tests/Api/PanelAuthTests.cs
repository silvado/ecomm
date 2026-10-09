using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ecommerce.Api.Auth;
using Ecommerce.Api.Panel;
using Ecommerce.Application.Identity;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>RF07 — login, sessão, perfis Dono/Operador e gestão de usuários do painel.</summary>
public sealed class PanelAuthTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    // ---------- Login ----------

    [Fact]
    public async Task Login_abre_sessao_na_unica_loja_com_cookie_protegido()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.CreateUserAsync(tenant.Id, TenantRole.Owner, Password);

        var login = await LoginAsync(api.PanelClient(), owner.Email, Password);

        Assert.Equal(HttpStatusCode.OK, login.Response.StatusCode);
        Assert.Equal(tenant.Id, login.Session!.Tenant!.Id);
        Assert.Equal("owner", login.Session.Tenant.Role);
        Assert.False(string.IsNullOrEmpty(login.Session.AccessToken));
        var setCookie = Assert.Single(login.Response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(AuthEndpoints.RefreshCookie, StringComparison.Ordinal));
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/auth", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(login.Cookie!, await login.Response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Senha_errada_e_email_inexistente_tem_a_mesma_resposta()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.CreateUserAsync(tenant.Id, TenantRole.Owner, Password);
        var client = api.PanelClient();

        var wrong = await LoginAsync(client, owner.Email, "senha-errada-123");
        var unknown = await LoginAsync(client, "ninguem@pecas.test", Password);

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.Response.StatusCode);
        Assert.Equal(await TitleAsync(wrong.Response), await TitleAsync(unknown.Response));
    }

    [Fact]
    public async Task Quinta_senha_errada_bloqueia_ate_a_senha_certa_e_desbloqueia_depois_de_15_minutos()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.CreateUserAsync(tenant.Id, TenantRole.Owner, Password);
        var client = api.PanelClient();

        for (var i = 0; i < 4; i++) await LoginAsync(client, owner.Email, "senha-errada-123");
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, owner.Email, Password)).Response.StatusCode); // 4 erros não bloqueiam e o acerto zera

        client = api.PanelClient(); // outro IP: o limite por IP não interfere na contagem por conta
        for (var i = 0; i < UserAccount.MaxFailedLogins; i++) await LoginAsync(client, owner.Email, "senha-errada-123");
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, owner.Email, Password)).Response.StatusCode);

        await api.SqlAsync($"UPDATE user_accounts SET locked_until = now() - interval '1 second' WHERE id = {owner.Id}");
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(api.PanelClient(), owner.Email, Password)).Response.StatusCode);
    }

    [Fact]
    public async Task Senhas_erradas_em_paralelo_bloqueiam_como_em_sequencia()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.CreateUserAsync(tenant.Id, TenantRole.Owner, Password);

        await Task.WhenAll(Enumerable.Range(0, UserAccount.MaxFailedLogins)
            .Select(_ => LoginAsync(api.PanelClient(), owner.Email, "senha-errada-123")));

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(api.PanelClient(), owner.Email, Password)).Response.StatusCode);
    }

    [Fact]
    public async Task Excesso_de_tentativas_do_mesmo_ip_recebe_429()
    {
        var client = api.PanelClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++) statuses.Add((await LoginAsync(client, $"x{i}@pecas.test", Password)).Response.StatusCode);

        Assert.All(statuses.Take(10), s => Assert.Equal(HttpStatusCode.Unauthorized, s));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[10]);
    }

    // ---------- Sessão ----------

    [Fact]
    public async Task Renovacao_gira_o_token_e_token_antigo_reapresentado_derruba_a_sessao()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.CreateUserAsync(tenant.Id, TenantRole.Owner, Password);
        var client = api.PanelClient();
        var first = (await LoginAsync(client, owner.Email, Password)).Cookie!;

        var second = await RefreshAsync(client, first);
        Assert.Equal(HttpStatusCode.OK, second.Response.StatusCode);
        Assert.NotEqual(first, second.Cookie);
        Assert.Equal(tenant.Id, second.Session!.Tenant!.Id);

        // Fora da janela de corrida entre abas, reapresentar o token usado é sinal de cópia.
        await api.SqlAsync($"UPDATE refresh_tokens SET used_at = now() - interval '5 minutes' WHERE user_id = {owner.Id} AND used_at IS NOT NULL");
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, first)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, second.Cookie!)).Response.StatusCode);
    }

    [Fact]
    public async Task Renovacoes_simultaneas_da_mesma_sessao_tem_um_vencedor_e_nao_derrubam_a_sessao()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.CreateUserAsync(tenant.Id, TenantRole.Owner, Password);
        var client = api.PanelClient();
        var cookie = (await LoginAsync(client, owner.Email, Password)).Cookie!;

        // Disparo realmente paralelo: todas leem o token como "não usado" antes de qualquer uma gravar.
        using var start = new ManualResetEventSlim();
        var pending = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return RefreshAsync(api.PanelClient(), cookie);
        })).ToList();
        start.Set();
        var results = await Task.WhenAll(pending);

        var winner = Assert.Single(results, r => r.Response.StatusCode == HttpStatusCode.OK);
        Assert.All(results.Where(r => r != winner), r => Assert.Equal(HttpStatusCode.Conflict, r.Response.StatusCode));
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(client, winner.Cookie!)).Response.StatusCode);
    }

    [Fact]
    public async Task Logout_encerra_a_sessao()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.CreateUserAsync(tenant.Id, TenantRole.Owner, Password);
        var client = api.PanelClient();
        var cookie = (await LoginAsync(client, owner.Email, Password)).Cookie!;

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logout.Headers.Add("Cookie", $"{AuthEndpoints.RefreshCookie}={cookie}");
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(logout)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, cookie)).Response.StatusCode);
    }

    [Fact]
    public async Task Usuario_de_duas_lojas_escolhe_a_loja_e_nao_escolhe_loja_alheia()
    {
        var (a, b, alheia) = (await api.CreateTenantAsync(), await api.CreateTenantAsync(), await api.CreateTenantAsync());
        var user = await api.CreateUserAsync(a.Id, TenantRole.Owner, Password);
        await api.AddMembershipAsync(b.Id, user.Id, TenantRole.Operator);
        var client = api.PanelClient();

        var login = await LoginAsync(client, user.Email, Password);
        Assert.Null(login.Session!.Tenant);
        Assert.Equal(2, login.Session.Tenants.Count);
        var noStore = await GetAsync(client, login.Session.AccessToken, "/api/painel/eu");
        Assert.Equal(PanelProblemTypes.ChooseStore, (await noStore.Content.ReadFromJsonAsync<ProblemDetails>())!.Type);

        var chosen = await SelectTenantAsync(client, login.Cookie!, b.Id);
        Assert.Equal(HttpStatusCode.OK, chosen.Response.StatusCode);
        var me = await (await GetAsync(client, chosen.Session!.AccessToken, "/api/painel/eu")).Content.ReadFromJsonAsync<MeDto>();
        Assert.Equal(b.Id, me!.Tenant.Id);
        Assert.Equal("operator", me.Tenant.Role);

        Assert.Equal(HttpStatusCode.Forbidden, (await SelectTenantAsync(client, chosen.Cookie!, alheia.Id)).Response.StatusCode);
    }

    // ---------- Isolamento ----------

    [Fact]
    public async Task Token_valido_com_loja_da_qual_o_usuario_nao_participa_e_recusado()
    {
        var (a, b) = (await api.CreateTenantAsync(), await api.CreateTenantAsync());
        var owner = await api.CreateUserAsync(a.Id, TenantRole.Owner, Password);

        // Mesmo um token assinado pela API (ex.: emitido antes de o vínculo ser removido) não abre a loja B.
        var (token, _) = api.Services.GetRequiredService<AccessTokens>().Issue(new Session(
            owner.Id, owner.Email, false, new TenantAccess(b.Id, b.Slug, b.TradeName, TenantRole.Owner), [], "x"));

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(api.PanelClient(), token, "/api/painel/usuarios")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(api.PanelClient(), token, "/api/painel/eu")).StatusCode);
    }

    [Fact]
    public async Task Token_assinado_com_outra_chave_e_recusado()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.CreateUserAsync(tenant.Id, TenantRole.Owner, Password);
        var forged = new AccessTokens(Microsoft.Extensions.Options.Options.Create(new AuthOptions
        {
            SigningKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
        }), TimeProvider.System).Issue(new Session(
            owner.Id, owner.Email, false, new TenantAccess(tenant.Id, tenant.Slug, tenant.TradeName, TenantRole.Owner), [], "x")).Token;

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(api.PanelClient(), forged, "/api/painel/eu")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.PanelClient().GetAsync("/api/painel/eu")).StatusCode);
    }

    [Fact]
    public async Task Dono_de_outra_loja_nao_altera_usuario_desta()
    {
        var (a, b) = (await api.CreateTenantAsync(), await api.CreateTenantAsync());
        var operatorA = await api.CreateUserAsync(a.Id, TenantRole.Operator, Password);
        var ownerB = await OwnerSessionAsync(b);

        var client = api.PanelClient();
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(client, ownerB, HttpMethod.Delete, $"/api/painel/usuarios/{operatorA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(client, ownerB, HttpMethod.Put, $"/api/painel/usuarios/{operatorA.Id}", new { role = "owner" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(client, ownerB, HttpMethod.Post, $"/api/painel/usuarios/{operatorA.Id}/desbloquear")).StatusCode);
        var listed = await (await GetAsync(client, ownerB, "/api/painel/usuarios")).Content.ReadFromJsonAsync<List<StoreUserDto>>();
        Assert.DoesNotContain(listed!, u => u.UserId == operatorA.Id);
        Assert.True(await api.WithPlatformAsync(db => db.TenantMemberships.AnyAsync(m => m.UserId == operatorA.Id && m.TenantId == a.Id)));
    }

    // ---------- Perfis ----------

    [Fact]
    public async Task Operador_nao_acessa_cofre_nem_usuarios_e_dono_acessa()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await OwnerSessionAsync(tenant);
        var op = await api.CreateUserAsync(tenant.Id, TenantRole.Operator, Password);
        var opToken = (await LoginAsync(api.PanelClient(), op.Email, Password)).Session!.AccessToken;
        var client = api.PanelClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(client, opToken, "/api/painel/cofre")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(client, opToken, "/api/painel/usuarios")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, opToken, HttpMethod.Post, "/api/painel/usuarios",
            new { email = "novo@pecas.test", role = "operator", temporaryPassword = "provisoria-123" })).StatusCode);
        var me = await (await GetAsync(client, opToken, "/api/painel/eu")).Content.ReadFromJsonAsync<MeDto>();
        Assert.Equal(["catalogManage", "ordersManage", "supportManage"], me!.Tenant.Permissions);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, owner, "/api/painel/cofre")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, owner, "/api/painel/usuarios")).StatusCode);
    }

    [Fact]
    public async Task Usuario_removido_perde_o_acesso_e_a_sessao()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await OwnerSessionAsync(tenant);
        var op = await api.CreateUserAsync(tenant.Id, TenantRole.Operator, Password);
        var client = api.PanelClient();
        var opLogin = await LoginAsync(client, op.Email, Password);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, opLogin.Session!.AccessToken, "/api/painel/eu")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(client, owner, HttpMethod.Delete, $"/api/painel/usuarios/{op.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(client, opLogin.Session.AccessToken, "/api/painel/eu")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, opLogin.Cookie!)).Response.StatusCode);
    }

    [Fact]
    public async Task Removido_de_uma_loja_segue_na_outra_e_nao_volta_a_removida()
    {
        var (a, b) = (await api.CreateTenantAsync(), await api.CreateTenantAsync());
        var ownerA = await OwnerSessionAsync(a);
        var user = await api.CreateUserAsync(a.Id, TenantRole.Operator, Password);
        await api.AddMembershipAsync(b.Id, user.Id, TenantRole.Operator);
        var client = api.PanelClient();
        var inA = await SelectTenantAsync(client, (await LoginAsync(client, user.Email, Password)).Cookie!, a.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(client, ownerA, HttpMethod.Delete, $"/api/painel/usuarios/{user.Id}")).StatusCode);

        var refreshed = await RefreshAsync(client, inA.Cookie!);
        Assert.Equal(HttpStatusCode.OK, refreshed.Response.StatusCode);
        Assert.Equal(b.Id, refreshed.Session!.Tenant!.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await SelectTenantAsync(client, refreshed.Cookie!, a.Id)).Response.StatusCode);
    }

    [Fact]
    public async Task Mudanca_de_papel_vale_na_proxima_requisicao()
    {
        var tenant = await api.CreateTenantAsync(Plan.Professional);
        var owner = await OwnerSessionAsync(tenant);
        var other = await api.CreateUserAsync(tenant.Id, TenantRole.Owner, Password);
        var client = api.PanelClient();
        var otherToken = (await LoginAsync(client, other.Email, Password)).Session!.AccessToken;
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, otherToken, "/api/painel/usuarios")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(client, owner, HttpMethod.Put, $"/api/painel/usuarios/{other.Id}", new { role = "operator" })).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(client, otherToken, "/api/painel/usuarios")).StatusCode);
    }

    [Fact]
    public async Task Loja_nao_fica_sem_dono()
    {
        var tenant = await api.CreateTenantAsync();
        var ownerUser = await api.CreateUserAsync(tenant.Id, TenantRole.Owner, Password);
        var owner = (await LoginAsync(api.PanelClient(), ownerUser.Email, Password)).Session!.AccessToken;
        var client = api.PanelClient();

        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, owner, HttpMethod.Delete, $"/api/painel/usuarios/{ownerUser.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, owner, HttpMethod.Put, $"/api/painel/usuarios/{ownerUser.Id}", new { role = "operator" })).StatusCode);
    }

    [Fact]
    public async Task Dois_donos_rebaixando_um_ao_outro_ao_mesmo_tempo_deixam_um_dono()
    {
        var tenant = await api.CreateTenantAsync();
        var first = await api.CreateUserAsync(tenant.Id, TenantRole.Owner, Password);
        var second = await api.CreateUserAsync(tenant.Id, TenantRole.Owner, Password);
        var firstToken = (await LoginAsync(api.PanelClient(), first.Email, Password)).Session!.AccessToken;
        var secondToken = (await LoginAsync(api.PanelClient(), second.Email, Password)).Session!.AccessToken;

        await Task.WhenAll(
            SendAsync(api.PanelClient(), firstToken, HttpMethod.Put, $"/api/painel/usuarios/{second.Id}", new { role = "operator" }),
            SendAsync(api.PanelClient(), secondToken, HttpMethod.Put, $"/api/painel/usuarios/{first.Id}", new { role = "operator" }));

        Assert.Equal(1, await api.WithPlatformAsync(db => db.TenantMemberships.CountAsync(m => m.TenantId == tenant.Id && m.Role == TenantRole.Owner)));
    }

    // ---------- Gestão de usuários e plano ----------

    [Fact]
    public async Task Limite_de_usuarios_do_plano_e_respeitado()
    {
        var tenant = await api.CreateTenantAsync(Plan.Essential); // 2 usuários
        var owner = await OwnerSessionAsync(tenant);
        var client = api.PanelClient();

        Assert.Equal(HttpStatusCode.Created, (await AddUserAsync(client, owner, "primeiro")).StatusCode);
        var blocked = await AddUserAsync(client, owner, "segundo");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("Limite de usuários", await TitleAsync(blocked), StringComparison.Ordinal);

        await api.SqlAsync($"UPDATE tenants SET plan_code = {Plan.Professional} WHERE id = {tenant.Id}");
        Assert.Equal(HttpStatusCode.Created, (await AddUserAsync(client, owner, "segundo")).StatusCode);
    }

    [Fact]
    public async Task Cadastros_simultaneos_nao_furam_o_limite_do_plano()
    {
        var tenant = await api.CreateTenantAsync(Plan.Essential); // dono + 1
        var owner = await OwnerSessionAsync(tenant);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => AddUserAsync(api.PanelClient(), owner, $"paralelo{i}")));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(2, await api.WithPlatformAsync(db => db.TenantMemberships.CountAsync(m => m.TenantId == tenant.Id)));
    }

    [Fact]
    public async Task Novo_usuario_precisa_trocar_a_senha_provisoria_antes_de_usar_o_painel()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await OwnerSessionAsync(tenant);
        var client = api.PanelClient();
        var email = $"novo{Guid.NewGuid():N}@pecas.test";
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(client, owner, HttpMethod.Post, "/api/painel/usuarios",
            new { email, role = "operator", temporaryPassword = "provisoria-123" })).StatusCode);

        var login = await LoginAsync(client, email, "provisoria-123");
        Assert.True(login.Session!.MustChangePassword);
        var blocked = await GetAsync(client, login.Session.AccessToken, "/api/painel/eu");
        Assert.Equal(PanelProblemTypes.PasswordChangeRequired, (await blocked.Content.ReadFromJsonAsync<ProblemDetails>())!.Type);

        var wrongCurrent = await SendAsync(client, login.Session.AccessToken, HttpMethod.Put, "/api/auth/senha",
            new { currentPassword = "nao-e-a-provisoria", newPassword = "minha-senha-nova-1" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongCurrent.StatusCode);

        var weak = await SendAsync(client, login.Session.AccessToken, HttpMethod.Put, "/api/auth/senha",
            new { currentPassword = "provisoria-123", newPassword = "curta" });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

        var changed = await SendAsync(client, login.Session.AccessToken, HttpMethod.Put, "/api/auth/senha",
            new { currentPassword = "provisoria-123", newPassword = "minha-senha-nova-1" });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var session = await changed.Content.ReadFromJsonAsync<SessionDto>();
        Assert.False(session!.MustChangePassword);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, session.AccessToken, "/api/painel/eu")).StatusCode);

        // A troca encerra as outras sessões.
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, login.Cookie!)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, email, "minha-senha-nova-1")).Response.StatusCode);
    }

    [Fact]
    public async Task Email_que_ja_tem_conta_ganha_so_o_vinculo_e_mantem_a_senha()
    {
        var (a, b) = (await api.CreateTenantAsync(), await api.CreateTenantAsync());
        var existing = await api.CreateUserAsync(a.Id, TenantRole.Owner, Password);
        var ownerB = await OwnerSessionAsync(b);
        var client = api.PanelClient();
        var otherPassword = "outra-" + Guid.NewGuid().ToString("N");

        var added = await SendAsync(client, ownerB, HttpMethod.Post, "/api/painel/usuarios",
            new { email = existing.Email.ToUpperInvariant(), role = "operator", temporaryPassword = otherPassword });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, existing.Email, Password)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, existing.Email, otherPassword)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, ownerB, HttpMethod.Post, "/api/painel/usuarios",
            new { email = existing.Email, role = "operator", temporaryPassword = otherPassword })).StatusCode);
    }

    [Fact]
    public async Task Dono_desbloqueia_operador()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await OwnerSessionAsync(tenant);
        var op = await api.CreateUserAsync(tenant.Id, TenantRole.Operator, Password);
        var client = api.PanelClient();
        for (var i = 0; i < UserAccount.MaxFailedLogins; i++) await LoginAsync(client, op.Email, "senha-errada-123");
        var listed = await (await GetAsync(client, owner, "/api/painel/usuarios")).Content.ReadFromJsonAsync<List<StoreUserDto>>();
        Assert.True(listed!.Single(u => u.UserId == op.Id).LockedOut);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(client, owner, HttpMethod.Post, $"/api/painel/usuarios/{op.Id}/desbloquear")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(api.PanelClient(), op.Email, Password)).Response.StatusCode);
    }

    [Fact]
    public async Task Senhas_e_tokens_nao_aparecem_nos_logs()
    {
        var tenant = await api.CreateTenantAsync();
        var secret = "senha-unica-" + Guid.NewGuid().ToString("N");
        var owner = await api.CreateUserAsync(tenant.Id, TenantRole.Owner, secret);
        var client = api.PanelClient();

        await LoginAsync(client, owner.Email, secret + "-errada");
        var login = await LoginAsync(client, owner.Email, secret);
        var refreshed = await RefreshAsync(client, login.Cookie!);

        Assert.NotEmpty(api.Logs.AllText);
        Assert.DoesNotContain(secret, api.Logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(login.Cookie!, api.Logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(refreshed.Cookie!, api.Logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(login.Session!.AccessToken, api.Logs.AllText, StringComparison.Ordinal);
    }

    // ---------- Apoio ----------

    private Task<string> OwnerSessionAsync(Tenant tenant) => api.SessionForAsync(tenant);

    private static Task<HttpResponseMessage> AddUserAsync(HttpClient client, string token, string prefix) =>
        SendAsync(client, token, HttpMethod.Post, "/api/painel/usuarios",
            new { email = $"{prefix}{Guid.NewGuid():N}@pecas.test", role = "operator", temporaryPassword = "provisoria-123" });
}
