using System.Net;
using System.Net.Http.Json;
using Ecommerce.Application.Channels;
using Ecommerce.Application.Vault;
using Ecommerce.Domain.Channels;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Vault;
using Ecommerce.Infrastructure.Channels.MercadoLivre;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Platform;
using Ecommerce.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF21 CA1/CA2 — conexão da conta do Mercado Livre (ML simulado): OAuth com state de uso único e PKCE, tokens no cofre,
/// renovação antes de vencer sem gastar o refresh duas vezes, "reautorizar" quando o ML recusa, desconectar e isolamento.
/// </summary>
public sealed class MercadoLivreConnectionTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record StartDto(string AuthorizationUrl);
    private sealed record ConnectionDto(Guid Id, string Channel, string ExternalAccountId, string? AccountName, string Status, string? StatusReason);
    private sealed record OverviewDto(bool MercadoLivreAvailable, bool MercadoLivreInPlan, List<ConnectionDto> Connections);
    private sealed record SummaryDto(int PendingFailures, int FailedCallsLast24h, int ChannelsNeedingReauth);

    private static string NewSeller() => Random.Shared.NextInt64(100_000_000, 999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task<string> StartAsync(string token)
    {
        var response = await SendAsync(api.PanelClient(), token, HttpMethod.Post, "/api/painel/canais/mercado-livre/conectar");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var url = (await response.Content.ReadFromJsonAsync<StartDto>())!.AuthorizationUrl;
        Assert.StartsWith(FakeMercadoLivreAuth.SimulationPath + "?state=", url, StringComparison.Ordinal);
        Assert.Contains("&code_challenge=", url, StringComparison.Ordinal);
        return Uri.UnescapeDataString(url.Split("state=")[1].Split('&')[0]);
    }

    private Task<HttpResponseMessage> ReturnAsync(string token, string? code, string? state, string? error = null) =>
        SendAsync(api.PanelClient(), token, HttpMethod.Post, "/api/painel/canais/mercado-livre/retorno", new { code, state, error });

    private async Task<ConnectionDto> ConnectAsync(string token, string seller)
    {
        var response = await ReturnAsync(token, FakeMercadoLivreAuth.CodePrefix + seller, await StartAsync(token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ConnectionDto>())!;
    }

    private async Task<T> InTenantAsync<T>(Guid tenantId, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        return await action(scope.ServiceProvider);
    }

    private Task<ChannelConnection> ConnectionAsync(Guid tenantId, Guid id) =>
        InTenantAsync(tenantId, sp => sp.GetRequiredService<TenantDbContext>().ChannelConnections.AsNoTracking().SingleAsync(c => c.Id == id));

    private Task<OAuthTokens?> TokensAsync(Guid tenantId, string seller) =>
        InTenantAsync(tenantId, sp => MercadoLivreCredentials.ReadAsync(sp.GetRequiredService<ISecretVault>(), seller, CancellationToken.None));

    /// <summary>Faz o acesso guardado vencer daqui a pouco (como se tivessem passado ~5 h).</summary>
    private Task<bool> MakeDueAsync(Guid tenantId, string seller) => InTenantAsync(tenantId, async sp =>
    {
        var tokens = (await MercadoLivreCredentials.ReadAsync(sp.GetRequiredService<ISecretVault>(), seller, CancellationToken.None))!;
        await MercadoLivreCredentials.SaveAsync(sp.GetRequiredService<ISecretVault>(), tokens with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(3) }, CancellationToken.None);
        return true;
    });

    private Task<int> LogCountAsync(Guid tenantId, string operation, bool success = true) =>
        InTenantAsync(tenantId, sp => sp.GetRequiredService<TenantDbContext>().IntegrationLogs
            .CountAsync(l => l.Channel == SalesChannel.MercadoLivre && l.Operation == operation && l.Success == success));

    [Fact]
    public async Task Conecta_pelo_OAuth_guarda_tokens_no_cofre_e_liga_a_conta_a_loja()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var seller = NewSeller();

        var overview = (await (await GetAsync(api.PanelClient(), owner, "/api/painel/canais")).Content.ReadFromJsonAsync<OverviewDto>())!;
        Assert.Equal((true, true, 0), (overview.MercadoLivreAvailable, overview.MercadoLivreInPlan, overview.Connections.Count));

        var state = await StartAsync(owner);
        var response = await ReturnAsync(owner, FakeMercadoLivreAuth.CodePrefix + seller, state);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var connection = (await response.Content.ReadFromJsonAsync<ConnectionDto>())!;
        Assert.Equal((SalesChannel.MercadoLivre, seller, "VENDEDOR_TESTE_" + seller, "active"),
            (connection.Channel, connection.ExternalAccountId, connection.AccountName, connection.Status));
        var tokens = (await TokensAsync(tenant.Id, seller))!;
        Assert.True(tokens.ExpiresAt > DateTimeOffset.UtcNow.AddHours(5));
        Assert.Equal(tokens.ExpiresAt, (await ConnectionAsync(tenant.Id, connection.Id)).TokenExpiresAt!.Value, TimeSpan.FromMilliseconds(1));
        // Cofre: só metadados, sem dica (o painel nunca vê token).
        var secret = (await InTenantAsync(tenant.Id, sp => sp.GetRequiredService<ISecretVault>().ListAsync())).Single(s => s.Kind == SecretKind.OAuthToken);
        Assert.Equal((MercadoLivreCredentials.SecretName(seller), string.Empty), (secret.Name, secret.Hint));
        // Mapa da plataforma: avisos do ML deste vendedor vão para esta loja (RF21 CA5).
        await using (var scope = api.Services.CreateAsyncScope())
            Assert.Equal(tenant.Id, await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().ChannelAccounts
                .Where(a => a.Channel == SalesChannel.MercadoLivre && a.ExternalAccountId == seller).Select(a => a.TenantId).SingleAsync());
        Assert.Equal(1, await LogCountAsync(tenant.Id, "autorizar conta"));

        // O mesmo state não serve duas vezes.
        var again = await ReturnAsync(owner, FakeMercadoLivreAuth.CodePrefix + seller, state);
        Assert.Equal((HttpStatusCode.BadRequest, "Esta autorização venceu ou já foi usada. Clique em Conectar de novo."), (again.StatusCode, await TitleAsync(again)));
    }

    [Fact]
    public async Task Volta_negada_vencida_sem_state_ou_de_outra_loja_nao_conecta()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var other = await api.CreateTenantAsync();
        var otherOwner = await api.SessionForAsync(other);
        var seller = NewSeller();

        var denied = await ReturnAsync(owner, null, await StartAsync(owner), "access_denied");
        Assert.Equal("A autorização foi negada no Mercado Livre. Nada foi conectado.", await TitleAsync(denied));

        Assert.Equal(HttpStatusCode.BadRequest, (await ReturnAsync(owner, FakeMercadoLivreAuth.CodePrefix + seller, null)).StatusCode);

        var expired = await StartAsync(owner);
        await InTenantAsync(tenant.Id, sp => sp.GetRequiredService<TenantDbContext>().ChannelAuthorizations
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        Assert.Equal(HttpStatusCode.BadRequest, (await ReturnAsync(owner, FakeMercadoLivreAuth.CodePrefix + seller, expired)).StatusCode);

        // State começado na loja A não conclui na loja B (RLS + filtro).
        var fromA = await StartAsync(owner);
        Assert.Equal(HttpStatusCode.BadRequest, (await ReturnAsync(otherOwner, FakeMercadoLivreAuth.CodePrefix + seller, fromA)).StatusCode);

        // Código recusado pelo ML.
        var refused = await ReturnAsync(owner, "codigo-invalido", await StartAsync(owner));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("recusou o acesso", await TitleAsync(refused), StringComparison.Ordinal);

        Assert.Equal(0, await InTenantAsync(tenant.Id, sp => sp.GetRequiredService<TenantDbContext>().ChannelConnections.CountAsync()));
        Assert.Equal(0, await InTenantAsync(other.Id, sp => sp.GetRequiredService<TenantDbContext>().ChannelConnections.CountAsync()));
    }

    [Fact]
    public async Task Conta_de_outra_loja_ou_segunda_conta_na_mesma_loja_sao_recusadas()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var other = await api.CreateTenantAsync();
        var otherOwner = await api.SessionForAsync(other);
        var seller = NewSeller();
        await ConnectAsync(owner, seller);

        var taken = await ReturnAsync(otherOwner, FakeMercadoLivreAuth.CodePrefix + seller, await StartAsync(otherOwner));
        Assert.Equal((HttpStatusCode.Conflict, "Esta conta do Mercado Livre já está ligada a outra loja da plataforma."), (taken.StatusCode, await TitleAsync(taken)));

        var second = await ReturnAsync(owner, FakeMercadoLivreAuth.CodePrefix + NewSeller(), await StartAsync(owner));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.StartsWith("A loja já está ligada à conta VENDEDOR_TESTE_", await TitleAsync(second), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Renova_antes_de_vencer_e_chamadas_simultaneas_gastam_o_refresh_uma_vez_so()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var seller = NewSeller();
        var connection = await ConnectAsync(owner, seller);
        var first = (await TokensAsync(tenant.Id, seller))!;

        Assert.Equal(RenewOutcome.NotDue, await InTenantAsync(tenant.Id, sp => sp.GetRequiredService<MercadoLivreCredentials>().RenewIfDueAsync(connection.Id)));

        await MakeDueAsync(tenant.Id, seller);
        var tokens = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            InTenantAsync(tenant.Id, sp => sp.GetRequiredService<MercadoLivreCredentials>().AccessTokenAsync(connection.Id))));

        Assert.Single(tokens.Distinct());
        Assert.NotEqual(first.AccessToken, tokens[0]);
        var renewed = (await TokensAsync(tenant.Id, seller))!;
        Assert.NotEqual(first.RefreshToken, renewed.RefreshToken);
        Assert.Equal(1, await LogCountAsync(tenant.Id, "renovar acesso"));
        Assert.Equal(ChannelConnectionStatus.Active, (await ConnectionAsync(tenant.Id, connection.Id)).Status);

        // Pela fila (o Worker publica de hora em hora): só renova quem vence dentro da janela.
        await MakeDueAsync(tenant.Id, seller);
        await InTenantAsync(tenant.Id, sp => sp.GetRequiredService<TenantDbContext>().ChannelConnections.Where(c => c.Id == connection.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.TokenExpiresAt, DateTimeOffset.UtcNow.AddMinutes(3))));
        await api.Services.GetRequiredService<IMessageBus>().InvokeForTenantAsync(tenant.Id.ToString(), new RenewChannelCredentials());
        Assert.Equal(2, await LogCountAsync(tenant.Id, "renovar acesso"));
        Assert.True((await ConnectionAsync(tenant.Id, connection.Id)).TokenExpiresAt > DateTimeOffset.UtcNow.AddHours(5));
    }

    [Fact]
    public async Task ML_fora_do_ar_na_renovacao_nao_derruba_a_conexao()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var seller = NewSeller();
        var connection = await ConnectAsync(owner, seller);
        await MakeDueAsync(tenant.Id, seller);
        FakeMercadoLivreAuth.FailNext(seller, 1);

        await Assert.ThrowsAsync<ChannelConnectorException>(() =>
            InTenantAsync(tenant.Id, sp => sp.GetRequiredService<MercadoLivreCredentials>().RenewIfDueAsync(connection.Id)));

        Assert.Equal(ChannelConnectionStatus.Active, (await ConnectionAsync(tenant.Id, connection.Id)).Status);
        Assert.Equal(1, await LogCountAsync(tenant.Id, "renovar acesso", success: false));
        // O refresh antigo continua valendo: a próxima tentativa renova.
        Assert.Equal(RenewOutcome.Renewed, await InTenantAsync(tenant.Id, sp => sp.GetRequiredService<MercadoLivreCredentials>().RenewIfDueAsync(connection.Id)));
    }

    [Fact]
    public async Task Acesso_revogado_marca_reautorizar_com_alerta_e_conectar_de_novo_resolve()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var seller = NewSeller();
        var connection = await ConnectAsync(owner, seller);
        await MakeDueAsync(tenant.Id, seller);
        FakeMercadoLivreAuth.Revoke(seller);

        Assert.Equal(RenewOutcome.ReauthRequired, await InTenantAsync(tenant.Id, sp => sp.GetRequiredService<MercadoLivreCredentials>().RenewIfDueAsync(connection.Id)));

        var stored = await ConnectionAsync(tenant.Id, connection.Id);
        Assert.Equal(ChannelConnectionStatus.ReauthRequired, stored.Status);
        Assert.Contains("Conecte a conta de novo", stored.StatusReason, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ChannelAuthorizationRevokedException>(() =>
            InTenantAsync(tenant.Id, sp => sp.GetRequiredService<MercadoLivreCredentials>().AccessTokenAsync(connection.Id)));
        var summary = (await (await GetAsync(api.PanelClient(), owner, "/api/painel/integracoes/resumo")).Content.ReadFromJsonAsync<SummaryDto>())!;
        Assert.Equal(1, summary.ChannelsNeedingReauth);

        var again = await ConnectAsync(owner, seller);
        Assert.Equal((connection.Id, "active", (string?)null), (again.Id, again.Status, again.StatusReason));
    }

    [Fact]
    public async Task Desconectar_revoga_apaga_os_tokens_e_libera_a_conta()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var seller = NewSeller();
        var connection = await ConnectAsync(owner, seller);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(api.PanelClient(), owner, HttpMethod.Delete, $"/api/painel/canais/{connection.Id}")).StatusCode);

        Assert.Equal(ChannelConnectionStatus.Disabled, (await ConnectionAsync(tenant.Id, connection.Id)).Status);
        Assert.Null(await TokensAsync(tenant.Id, seller));
        Assert.Equal(1, await LogCountAsync(tenant.Id, "revogar acesso"));
        await using (var scope = api.Services.CreateAsyncScope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().ChannelAccounts.AnyAsync(a => a.ExternalAccountId == seller));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(api.PanelClient(), owner, HttpMethod.Delete, $"/api/painel/canais/{connection.Id}")).StatusCode);

        // Conectar a mesma conta de novo reativa a mesma conexão (os anúncios dela continuam ligados).
        var again = await ConnectAsync(owner, seller);
        Assert.Equal((connection.Id, "active"), (again.Id, again.Status));
    }

    [Fact]
    public async Task So_o_Dono_ve_e_conecta_canais()
    {
        var tenant = await api.CreateTenantAsync();
        var operador = await api.SessionForAsync(tenant, TenantRole.Operator);

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(api.PanelClient(), operador, "/api/painel/canais")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(api.PanelClient(), operador, HttpMethod.Post, "/api/painel/canais/mercado-livre/conectar")).StatusCode);
    }
}
