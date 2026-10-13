using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Ecommerce.Application.Channels;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Channels;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wolverine;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF27 — reconciliação: o que alguém mudou no canal volta a refletir o estoque local (a verdade), cada divergência vai
/// para o log de integração, anúncio sumido fica em erro e os anúncios são conferidos em lotes espaçados.
/// </summary>
public sealed class ChannelReconciliationTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record ListingDto(Guid Id, string? ExternalId, string Status, int? LastPushedQuantity, string? LastError, DateTimeOffset? LastReconciledAt);
    private sealed record LogDto(string Operation, bool Success, string? Reference, string? ErrorSummary);
    private sealed record LogPageDto(List<LogDto> Items);

    private sealed record Shop(Tenant Tenant, string Owner);

    private async Task<(Shop Shop, Guid Part, ListingDto Listing)> PublishedAsync(int quantity = 2, decimal price = 100m)
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        await SendAsync(api.PanelClient(), owner, HttpMethod.Post, "/api/painel/canal-simulado/conectar", new { conta = "rec-" + Guid.NewGuid().ToString("N")[..10] });
        var part = await api.CreatePartAsync(owner, price, quantity);
        var published = await SendAsync(api.PanelClient(), owner, HttpMethod.Post, "/api/painel/canal-simulado/publicar", new { pecaId = part });
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        return (new Shop(tenant, owner), part, Assert.Single((await published.Content.ReadFromJsonAsync<List<ListingDto>>())!));
    }

    private async Task ReconcileAsync(Shop shop)
    {
        await using var scope = api.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeForTenantAsync(shop.Tenant.Id.ToString(), new ReconcileChannels());
    }

    private async Task<ListingDto> WaitAsync(Shop shop, Guid part, Func<ListingDto, bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var listing = Assert.Single((await (await GetAsync(api.PanelClient(), shop.Owner, $"/api/painel/pecas/{part}/anuncios")).Content.ReadFromJsonAsync<List<ListingDto>>())!);
            if (done(listing)) return listing;
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) Assert.Fail($"Anúncio ainda {listing.Status}, conferido em {listing.LastReconciledAt}.");
            await Task.Delay(100);
        }
    }

    private async Task<List<LogDto>> LogAsync(Shop shop, Guid part) =>
        (await (await GetAsync(api.PanelClient(), shop.Owner, $"/api/painel/integracoes/log?peca={part}")).Content.ReadFromJsonAsync<LogPageDto>())!.Items;

    [Fact]
    public async Task Quantidade_e_preco_mexidos_no_canal_voltam_ao_estoque_local_e_a_divergencia_vai_para_o_log()
    {
        var (shop, part, listing) = await PublishedAsync(2, 100m);
        var id = listing.ExternalId!;
        FakeChannelConnector.SetRemote(new RemoteListing(id, RemoteListingStatus.Active, 9, 80m));

        await ReconcileAsync(shop);
        await WaitAsync(shop, part, l => l.LastReconciledAt is not null);
        var remote = FakeChannelConnector.Remote(id)!;
        Assert.Equal((RemoteListingStatus.Active, 2, 100m), (remote.Status, remote.Quantity, remote.Price));
        var entry = Assert.Single(await LogAsync(shop, part), l => l.Operation == "reconciliar anúncio");
        Assert.Contains("quantidade 9 → 2", entry.Reference, StringComparison.Ordinal);
        Assert.Contains("preço 80,00 → 100,00", entry.Reference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pausado_no_canal_com_estoque_reativa_e_sem_divergencia_nao_registra_nada()
    {
        var (shop, part, listing) = await PublishedAsync(3);
        FakeChannelConnector.SetRemote(new RemoteListing(listing.ExternalId!, RemoteListingStatus.Paused, 3, 100m));
        await ReconcileAsync(shop);
        await WaitAsync(shop, part, l => l.LastReconciledAt is not null);
        Assert.Equal(RemoteListingStatus.Active, FakeChannelConnector.Remote(listing.ExternalId!)!.Status);

        var first = (await WaitAsync(shop, part, _ => true)).LastReconciledAt;
        await ReconcileAsync(shop);
        await WaitAsync(shop, part, l => l.LastReconciledAt > first);
        Assert.Single(await LogAsync(shop, part), l => l.Operation == "reconciliar anúncio"); // a segunda conferência não achou nada
    }

    [Fact]
    public async Task Pausa_do_lojista_vale_tambem_no_canal()
    {
        var (shop, part, listing) = await PublishedAsync();
        await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Post, $"/api/painel/anuncios/{listing.Id}/pausar");
        FakeChannelConnector.SetRemote(new RemoteListing(listing.ExternalId!, RemoteListingStatus.Active, 2, 100m)); // reativado lá por fora

        await ReconcileAsync(shop);
        var after = await WaitAsync(shop, part, l => l.LastReconciledAt is not null);
        Assert.Equal("pausedByUser", after.Status);
        Assert.Equal(RemoteListingStatus.Paused, FakeChannelConnector.Remote(listing.ExternalId!)!.Status);
        Assert.Contains(await LogAsync(shop, part), l => l.Reference?.Contains("pausa do lojista", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Anuncio_sumido_do_canal_fica_em_erro()
    {
        var (shop, part, listing) = await PublishedAsync();
        FakeChannelConnector.RemoveRemote(listing.ExternalId!);
        // O simulado trata anúncio desconhecido como existente nas escritas; a leitura devolve "não existe".
        await ReconcileAsync(shop);
        var after = await WaitAsync(shop, part, l => l.Status == "error");
        Assert.Contains("não existe mais", after.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anuncios_sao_conferidos_em_lotes_espacados_e_o_agendador_pede_por_loja()
    {
        var (shop, _, _) = await PublishedAsync();
        var second = await api.CreatePartAsync(shop.Owner);
        var third = await api.CreatePartAsync(shop.Owner);
        await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Post, "/api/painel/canal-simulado/publicar", new { pecaId = second });
        await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Post, "/api/painel/canal-simulado/publicar", new { pecaId = third });

        await using (var scope = api.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantScope>().Set(shop.Tenant.Id);
            var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
            var options = Options.Create(new ReconciliationOptions { BatchSize = 1, BatchInterval = TimeSpan.FromSeconds(10) });
            var outgoing = await ChannelReconciliationHandler.Handle(new ReconcileChannels(), db, options, TimeProvider.System, CancellationToken.None);
            var messages = outgoing.ToList();
            Assert.Equal(3, messages.Count);
            Assert.Single(messages, m => m is ReconcileListing); // o primeiro vai já; os outros, agendados a cada 10 s
        }

        var scheduler = ActivatorUtilities.CreateInstance<ChannelReconciliationScheduler>(api.Services);
        Assert.True(await scheduler.ScheduleAllAsync(CancellationToken.None) >= 1);
    }
}
