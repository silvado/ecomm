using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Ecommerce.Domain.Channels;
using Ecommerce.Domain.Integrations;
using Ecommerce.Domain.Payments;
using Ecommerce.Infrastructure.Channels.MercadoLivre;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF21 CA4/CA5 — avisos do Mercado Livre: 200 na hora, gravados com chave única, roteados para a loja pelo user_id do
/// vendedor (mapa criado na conexão OAuth) e processados uma vez; aviso de anúncio da loja pede a conferência dele.
/// </summary>
public sealed class MercadoLivreNotificationTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private const string Webhook = "/api/webhooks/canais/" + SalesChannel.MercadoLivre;

    private sealed record StartDto(string AuthorizationUrl);
    private sealed record ConnectionDto(Guid Id);
    private sealed record IdDto(Guid Id);

    private static string NewSeller() => Random.Shared.NextInt64(100_000_000, 999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Loja com a conta do ML conectada pelo fluxo OAuth (simulado).</summary>
    private async Task<(Guid TenantId, string Owner, string Seller, Guid ConnectionId)> ConnectedAsync()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var seller = NewSeller();
        var start = (await (await SendAsync(api.PanelClient(), owner, HttpMethod.Post, "/api/painel/canais/mercado-livre/conectar")).Content.ReadFromJsonAsync<StartDto>())!;
        var state = Uri.UnescapeDataString(start.AuthorizationUrl.Split("state=")[1].Split('&')[0]);
        var connected = await SendAsync(api.PanelClient(), owner, HttpMethod.Post, "/api/painel/canais/mercado-livre/retorno",
            new { code = FakeMercadoLivreAuth.CodePrefix + seller, state });
        Assert.Equal(HttpStatusCode.OK, connected.StatusCode);
        return (tenant.Id, owner, seller, (await connected.Content.ReadFromJsonAsync<ConnectionDto>())!.Id);
    }

    private Task<HttpResponseMessage> NotifyAsync(string json) =>
        api.ClientFor("api.plataforma.test", $"198.51.108.{Random.Shared.Next(1, 250)}")
            .PostAsync(Webhook, new StringContent(json, Encoding.UTF8, "application/json"));

    /// <summary>Formato da documentação (tópico geral, sem id: a chave sai de tópico + recurso + received).</summary>
    private static string Notification(string seller, string topic, string resource, string received, bool sellerAsText = false) =>
        $$"""
        {"resource":"{{resource}}","user_id":{{(sellerAsText ? $"\"{seller}\"" : seller)}},"topic":"{{topic}}","application_id":5503910054141466,
         "attempts":1,"sent":"{{received}}","received":"{{received}}"}
        """;

    private async Task<T> InTenantAsync<T>(Guid? tenantId, Func<TenantDbContext, Task<T>> query)
    {
        await using var scope = api.Services.CreateAsyncScope();
        if (tenantId is { } id) scope.ServiceProvider.GetRequiredService<TenantScope>().Set(id);
        return await query(scope.ServiceProvider.GetRequiredService<TenantDbContext>());
    }

    private static async Task<T> WaitAsync<T>(Func<Task<T?>> read, Func<T, bool> done, string what) where T : class
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (await read() is { } row && done(row)) return row;
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) Assert.Fail($"Esperando {what}.");
            await Task.Delay(100);
        }
    }

    private Task<ExternalEvent> ProcessedAsync(Guid tenantId, string resource) => WaitAsync(
        () => InTenantAsync(tenantId, db => db.ExternalEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Source == SalesChannel.MercadoLivre && e.ResourceId == resource)),
        e => e.Status != ExternalEventStatus.Received, $"o aviso {resource} processado na loja");

    [Fact]
    public async Task Aviso_do_vendedor_vai_para_a_loja_dele_uma_vez_so()
    {
        var (tenantId, _, seller, _) = await ConnectedAsync();
        var (otherTenant, _, _, _) = await ConnectedAsync();
        var resource = "/orders/" + NewSeller();
        var json = Notification(seller, "orders_v2", resource, "2026-10-17T12:00:00.106Z");

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => NotifyAsync(json)));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        // Vendas ainda não são processadas (próxima etapa): o aviso fica registrado como ignorado.
        Assert.Equal(ExternalEventStatus.Ignored, (await ProcessedAsync(tenantId, resource)).Status);
        Assert.Equal(1, await InTenantAsync(null, db => db.InboxEvents.CountAsync(e => e.Source == SalesChannel.MercadoLivre && e.Resource == resource)));
        Assert.Equal(1, await InTenantAsync(tenantId, db => db.ExternalEvents.CountAsync(e => e.ResourceId == resource)));
        Assert.Equal(0, await InTenantAsync(otherTenant, db => db.ExternalEvents.CountAsync(e => e.ResourceId == resource)));
    }

    [Fact]
    public async Task Aviso_de_anuncio_da_loja_pede_a_conferencia_com_o_canal()
    {
        var (tenantId, owner, seller, connectionId) = await ConnectedAsync();
        var created = await SendAsync(api.PanelClient(), owner, HttpMethod.Post, "/api/painel/pecas", new
        {
            internalCode = "ML-" + NewSeller()[..6], title = "Farol Gol G5", condition = "used", price = 350m, quantity = 1, oemCodes = Array.Empty<string>(),
        });
        var partId = (await created.Content.ReadFromJsonAsync<IdDto>())!.Id;
        var itemId = "MLB" + NewSeller();
        await InTenantAsync(tenantId, async db =>
        {
            var listing = new ChannelListing(tenantId, partId, connectionId, DateTimeOffset.UtcNow);
            listing.Published(itemId, 1, 350m, DateTimeOffset.UtcNow);
            db.ChannelListings.Add(listing);
            return await db.SaveChangesAsync();
        });

        // user_id como texto também vale (a documentação mostra os dois jeitos).
        Assert.Equal(HttpStatusCode.OK, (await NotifyAsync(Notification(seller, "items", "/items/" + itemId, "2026-10-17T12:01:00.000Z", sellerAsText: true))).StatusCode);

        Assert.Equal(ExternalEventStatus.Processed, (await ProcessedAsync(tenantId, "/items/" + itemId)).Status);
        Assert.Equal(1, await InTenantAsync(tenantId, db => db.IntegrationLogs.CountAsync(l => l.Channel == SalesChannel.MercadoLivre
            && l.Operation == "aviso: anúncio alterado" && l.Reference == itemId)));

        // Item que não é anúncio da loja: ignorado.
        await NotifyAsync(Notification(seller, "items", "/items/MLB999", "2026-10-17T12:02:00.000Z"));
        Assert.Equal(ExternalEventStatus.Ignored, (await ProcessedAsync(tenantId, "/items/MLB999")).Status);
    }

    [Fact]
    public async Task Vendedor_sem_loja_fica_sem_dono_no_inbox()
    {
        var resource = "/questions/" + NewSeller();

        Assert.Equal(HttpStatusCode.OK, (await NotifyAsync(Notification(NewSeller(), "questions", resource, "2026-10-17T12:03:00.000Z"))).StatusCode);

        await WaitAsync(() => InTenantAsync(null, db => db.InboxEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Resource == resource)),
            e => e.Status == InboxStatus.Unroutable, "o aviso sem dono");
    }

    [Theory]
    [InlineData("não é json")]
    [InlineData("""{"resource":"/orders/1","topic":"orders_v2"}""")]
    [InlineData("""{"resource":"/orders/1","user_id":"abc","topic":"orders_v2"}""")]
    public async Task Corpo_que_nao_e_aviso_do_ML_e_recusado(string body)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await NotifyAsync(body)).StatusCode);
    }
}
