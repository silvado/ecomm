using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ecommerce.Domain.Catalog;
using Ecommerce.Domain.Channels;
using Ecommerce.Domain.Identity;
using Ecommerce.Infrastructure.Channels.MercadoLivre;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Ecommerce.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF11 CA2/CA3 — importação dos anúncios que a loja já tem no Mercado Livre (ML simulado): anúncios ativos e pausados
/// viram peças vinculadas, com fotos e o SKU como código; mesmo código liga à peça existente (vale o estoque da loja);
/// cada variação vira uma peça em rascunho sem vínculo; encerrados ficam no relatório; reimportar não duplica; sem foto,
/// a peça fica em rascunho sem vínculo; só o
/// Dono importa e cada loja vê só as suas importações.
/// </summary>
public sealed class ChannelImportTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private const string Imports = "/api/painel/canais/mercado-livre/importacoes";

    private sealed record StartDto(string AuthorizationUrl);
    private sealed record IdDto(Guid Id);
    private sealed record IssueDto(string ExternalId, string Message);
    private sealed record ImportDto(Guid Id, string Status, int TotalItems, int ProcessedItems, int CreatedCount, int LinkedCount, int AlreadyLinkedCount,
        int RejectedCount, int CompatibilitiesCount, List<IssueDto> Issues, string? FailureReason);

    private sealed record Shop(Guid TenantId, string Owner, string Seller);

    private static string NewId() => Random.Shared.NextInt64(100_000_000, 999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task<Shop> ShopAsync(bool connect = true)
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var seller = NewId();
        if (connect)
        {
            var start = (await (await SendAsync(api.PanelClient(), owner, HttpMethod.Post, "/api/painel/canais/mercado-livre/conectar")).Content.ReadFromJsonAsync<StartDto>())!;
            var state = Uri.UnescapeDataString(start.AuthorizationUrl.Split("state=")[1].Split('&')[0]);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(api.PanelClient(), owner, HttpMethod.Post, "/api/painel/canais/mercado-livre/retorno",
                new { code = FakeMercadoLivreAuth.CodePrefix + seller, state })).StatusCode);
        }
        return new Shop(tenant.Id, owner, seller);
    }

    private async Task<T> InTenantAsync<T>(Guid tenantId, Func<TenantDbContext, Task<T>> query)
    {
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        return await query(scope.ServiceProvider.GetRequiredService<TenantDbContext>());
    }

    private async Task<ImportDto> RunAsync(Shop shop)
    {
        var started = await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Post, Imports);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var id = (await started.Content.ReadFromJsonAsync<ImportDto>())!.Id;
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var import = (await (await GetAsync(api.PanelClient(), shop.Owner, $"{Imports}/{id}")).Content.ReadFromJsonAsync<ImportDto>())!;
            if (import.Status is "completed" or "failed") return import;
            if (watch.Elapsed > TimeSpan.FromSeconds(30)) Assert.Fail($"A importação não terminou: {import.Status}, {import.ProcessedItems}/{import.TotalItems}.");
            await Task.Delay(200);
        }
    }

    /// <summary>Peça ativa cadastrada pelo painel, com foto.</summary>
    private async Task<Guid> ActivePartAsync(Shop shop, string code, int quantity)
    {
        var created = await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Post, "/api/painel/pecas", new
        {
            internalCode = code, title = "Retrovisor direito Gol", condition = "used", price = 180m, quantity,
        });
        var partId = (await created.Content.ReadFromJsonAsync<IdDto>())!.Id;
        using var photo = new HttpRequestMessage(HttpMethod.Post, $"/api/painel/pecas/{partId}/fotos") { Content = new ByteArrayContent(TestImages.Jpeg(800, 600)) };
        photo.Headers.Authorization = new AuthenticationHeaderValue("Bearer", shop.Owner);
        Assert.Equal(HttpStatusCode.Created, (await api.PanelClient().SendAsync(photo)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(api.PanelClient(), shop.Owner, HttpMethod.Put, $"/api/painel/pecas/{partId}/situacao", new { status = "active" })).StatusCode);
        return partId;
    }

    [Fact]
    public async Task Anuncios_viram_pecas_vinculadas_e_reimportar_nao_duplica()
    {
        var shop = await ShopAsync();
        var sku = "FAR-" + NewId()[..5];
        var active = FakeMercadoLivreApi.Seed(shop.Seller, "Farol dianteiro esquerdo Gol G5", 350m, 2, sku: sku, pictures: 2,
            description: "Farol original, sem trincas.", partNumber: "5U0941015",
            packageAttributes: [("SELLER_PACKAGE_LENGTH", "45 cm"), ("SELLER_PACKAGE_WIDTH", "35 cm"), ("SELLER_PACKAGE_HEIGHT", "25 cm"), ("SELLER_PACKAGE_WEIGHT", "2300 g")]);
        var paused = FakeMercadoLivreApi.Seed(shop.Seller, "Lanterna traseira Uno", 120m, 3, status: "paused", condition: "new", shippingDimensions: "10x20x30,500");
        var capCode = "CAL-" + NewId()[..5];
        var withVariations = FakeMercadoLivreApi.Seed(shop.Seller, "Calota aro 14", 40m, 8, sku: capCode,
            variations: [("Esquerda", 3, capCode + "E"), ("Direita", 5, null)]);
        var closed = FakeMercadoLivreApi.Seed(shop.Seller, "Para-choque vendido", 200m, 0, status: "closed");
        var existingCode = "RET-" + NewId()[..5];
        var existingPart = await ActivePartAsync(shop, existingCode, quantity: 1);
        var sameCode = FakeMercadoLivreApi.Seed(shop.Seller, "Retrovisor direito Gol", 180m, 5, sku: existingCode);

        var import = await RunAsync(shop);
        // 4 peças novas: 2 anúncios simples e as 2 variações da calota.
        Assert.Equal(("completed", 5, 4, 1, 1), (import.Status, import.TotalItems, import.CreatedCount, import.LinkedCount, import.RejectedCount));
        Assert.Contains(import.Issues, i => i.ExternalId == withVariations && i.Message.Contains("2 entraram como peças em rascunho", StringComparison.Ordinal));
        Assert.Contains(import.Issues, i => i.ExternalId == closed && i.Message.Contains("encerrado", StringComparison.Ordinal));
        Assert.Contains(import.Issues, i => i.ExternalId == sameCode && i.Message.Contains("vale o estoque da loja", StringComparison.Ordinal));

        var parts = await InTenantAsync(shop.TenantId, db => (from l in db.ChannelListings
                                                              join p in db.Parts.Include(p => p.Photos).Include(p => p.OemCodes) on l.PartId equals p.Id
                                                              join s in db.Stocks on p.Id equals s.PartId
                                                              select new { l.ExternalId, l.Status, Part = p, s.OnHand }).ToListAsync());
        var farol = parts.Single(p => p.ExternalId == active);
        Assert.Equal((sku, PartStatus.Active, ListingStatus.Active, 2, 2), (farol.Part.InternalCode, farol.Part.Status, farol.Status, farol.OnHand, farol.Part.Photos.Count));
        Assert.Equal(("Farol original, sem trincas.", 350m, PartCondition.Used), (farol.Part.Description, farol.Part.Price, farol.Part.Condition));
        Assert.Equal("5U0941015", Assert.Single(farol.Part.OemCodes).Code);
        Assert.Equal((45, 35, 25, 2300), (farol.Part.LengthCm, farol.Part.WidthCm, farol.Part.HeightCm, farol.Part.WeightG));
        var lanterna = parts.Single(p => p.ExternalId == paused);
        Assert.Equal((paused, ListingStatus.PausedByUser, PartCondition.New, 3), (lanterna.Part.InternalCode, lanterna.Status, lanterna.Part.Condition, lanterna.OnHand));
        Assert.Equal((30, 20, 10, 500), (lanterna.Part.LengthCm, lanterna.Part.WidthCm, lanterna.Part.HeightCm, lanterna.Part.WeightG));
        Assert.Null(parts.Single(p => p.ExternalId == sameCode).Part.LengthCm); // a peça existente não ganhou medidas
        var linked = parts.Single(p => p.ExternalId == sameCode);
        Assert.Equal((existingPart, 1), (linked.Part.Id, linked.OnHand)); // a peça existente não mudou

        // Variações: uma peça em rascunho por variação, com o saldo dela, sem vínculo com o anúncio.
        var caps = await InTenantAsync(shop.TenantId, db => (from p in db.Parts.Include(p => p.Photos)
                                                             join s in db.Stocks on p.Id equals s.PartId
                                                             where p.InternalCode.StartsWith(capCode)
                                                             orderby p.Title descending
                                                             select new { Part = p, s.OnHand }).ToListAsync());
        Assert.Equal([("Calota aro 14 - Esquerda", 3), ("Calota aro 14 - Direita", 5)], caps.Select(c => (c.Part.Title, c.OnHand)).ToList());
        Assert.Equal(capCode + "E", caps[0].Part.InternalCode); // SKU da variação
        Assert.StartsWith(capCode + "-", caps[1].Part.InternalCode, StringComparison.Ordinal); // sem SKU: o do anúncio com o id da variação
        Assert.All(caps, c => Assert.Equal((PartStatus.Draft, 1), (c.Part.Status, c.Part.Photos.Count)));
        Assert.DoesNotContain(parts, p => p.ExternalId == withVariations);

        // Vale o estoque da loja: o anúncio de 5 passa a 1.
        var watch = Stopwatch.StartNew();
        while ((int?)FakeMercadoLivreApi.Item(sameCode)!["available_quantity"] != 1)
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) Assert.Fail("O anúncio não recebeu o estoque da loja.");
            await Task.Delay(200);
        }
        Assert.Equal("paused", (string?)FakeMercadoLivreApi.Item(paused)!["status"]); // pausado lá continua pausado

        var again = await RunAsync(shop);
        Assert.Equal((5, 0, 0, 3, 2), (again.TotalItems, again.CreatedCount, again.LinkedCount, again.AlreadyLinkedCount, again.RejectedCount));
        Assert.Equal(3, await InTenantAsync(shop.TenantId, db => db.ChannelListings.CountAsync()));
    }

    [Fact]
    public async Task Veiculos_do_anuncio_que_casam_com_a_tabela_viram_compatibilidades_da_peca()
    {
        Assert.True((await api.ImportVehiclesAsync("""
            marca;modelo;motorizacao;ano_inicial;ano_final
            Volkswagen;Gol;1.0 8V Flex (G5);2009;2012
            Volkswagen;Gol;1.6 8V Flex (G5);2009;2012
            Ford;Ka;1.0 Ti-VCT Flex;2015;2021
            """)).Succeeded);
        var shop = await ShopAsync();
        var item = FakeMercadoLivreApi.Seed(shop.Seller, "Farol dianteiro Gol", 300m, 1, vehicles:
        [
            ("Volkswagen", "Gol", 2010, "1.6 8V"), ("Volkswagen", "Gol", 2011, "1.6 8V"),
            ("Ford", "Ka", 2016, null),
            ("Volkswagen", "Gol", 2010, null), // duas versões do Gol em 2010: sem motor, não dá para saber qual
            ("Fiat", "Uno", 2012, "1.0 Fire"), // fora da tabela
            ("", "", 0, null), // produto sem marca, modelo e ano
        ], catalogVehicles: 4);

        var import = await RunAsync(shop);
        Assert.Equal(("completed", 1, 2), (import.Status, import.CreatedCount, import.CompatibilitiesCount));
        var note = Assert.Single(import.Issues, i => i.ExternalId == item).Message;
        Assert.Contains("Volkswagen Gol 2010, Fiat Uno 2012 1.0 Fire", note, StringComparison.Ordinal);
        Assert.Contains("1 veículo do anúncio não pôde ser lido", note, StringComparison.Ordinal);
        Assert.Contains("4 veículos do anúncio vêm do catálogo do Mercado Livre", note, StringComparison.Ordinal);

        var saved = await InTenantAsync(shop.TenantId, db => (from c in db.PartCompatibilities
                                                              join p in db.Parts on c.PartId equals p.Id
                                                              join v in db.VehicleVersions on c.VehicleVersionId equals v.Id
                                                              where p.InternalCode == item
                                                              orderby v.Engine
                                                              select new { v.Engine, c.YearFrom, c.YearTo, c.Source }).ToListAsync());
        Assert.Equal([("1.0 Ti-VCT Flex", (int?)2016, (int?)2016, CompatibilitySource.MlImport), ("1.6 8V Flex (G5)", 2010, 2011, CompatibilitySource.MlImport)],
            saved.Select(s => (s.Engine, s.YearFrom, s.YearTo, s.Source)).ToList());
    }

    [Fact]
    public async Task Sem_foto_a_peca_fica_em_rascunho_sem_vinculo()
    {
        var shop = await ShopAsync();
        var item = FakeMercadoLivreApi.Seed(shop.Seller, "Motor de arranque Palio", 450m, 1);
        FakeMercadoLivreApi.BreakPictures(item);

        var import = await RunAsync(shop);
        Assert.Equal(("completed", 1), (import.Status, import.CreatedCount));
        Assert.Contains("entrou como rascunho", Assert.Single(import.Issues).Message, StringComparison.Ordinal);
        Assert.Equal(PartStatus.Draft, await InTenantAsync(shop.TenantId, db => db.Parts.Where(p => p.InternalCode == item).Select(p => p.Status).SingleAsync()));
        Assert.False(await InTenantAsync(shop.TenantId, db => db.ChannelListings.AnyAsync()));
        Assert.Equal("active", (string?)FakeMercadoLivreApi.Item(item)!["status"]); // o anúncio lá não foi mexido
    }

    [Fact]
    public async Task So_o_Dono_importa_com_conta_conectada_e_cada_loja_ve_as_suas()
    {
        var unconnected = await ShopAsync(connect: false);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(api.PanelClient(), unconnected.Owner, HttpMethod.Post, Imports)).StatusCode);

        var shop = await ShopAsync();
        var tenant = await api.WithPlatformAsync(db => db.Tenants.SingleAsync(t => t.Id == shop.TenantId));
        var operatorSession = await api.SessionForAsync(tenant, TenantRole.Operator);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(api.PanelClient(), operatorSession, HttpMethod.Post, Imports)).StatusCode);

        FakeMercadoLivreApi.Seed(shop.Seller, "Farol de milha Corsa", 90m, 1);
        var import = await RunAsync(shop);
        Assert.Equal(1, import.CreatedCount);

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(api.PanelClient(), unconnected.Owner, $"{Imports}/{import.Id}")).StatusCode);
        Assert.Empty((await (await GetAsync(api.PanelClient(), unconnected.Owner, Imports)).Content.ReadFromJsonAsync<List<ImportDto>>())!);
        Assert.False(await InTenantAsync(unconnected.TenantId, db => db.ChannelImports.IgnoreQueryFilters().AnyAsync()));
    }
}
