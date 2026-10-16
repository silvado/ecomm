using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Ecommerce.Application.Storage;
using Ecommerce.Application.Vault;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Privacy;
using Ecommerce.Domain.Vault;
using Ecommerce.Infrastructure.Integrations;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Privacy;
using Ecommerce.Infrastructure.Tenancy;
using Ecommerce.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF39 — exportação completa: ZIP com peças, fotos, pedidos e clientes; segredos do cofre ficam de fora; uma por vez;
/// download registrado e só por 7 dias; vencida, o arquivo some.
/// </summary>
public sealed class DataExportTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record ExportDto(Guid Id, string Status, string RequestedByEmail, long? SizeBytes, string? Error, bool CanDownload, DateTimeOffset? ExpiresAt);
    private sealed record AccessDto(string Action, string? Details);
    private sealed record AccessPageDto(List<AccessDto> Items);
    private sealed record PlacedDto(long Number);

    private async Task<T> InTenantAsync<T>(Guid tenantId, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        return await action(scope.ServiceProvider);
    }

    private async Task<ExportDto> ReadyAsync(string owner, Guid id)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var list = (await (await GetAsync(api.PanelClient(), owner, "/api/painel/privacidade/exportacoes")).Content.ReadFromJsonAsync<List<ExportDto>>())!;
            var export = list.Single(e => e.Id == id);
            if (export.Status != "pending") return export;
            if (watch.Elapsed > TimeSpan.FromSeconds(30)) Assert.Fail("Exportação ainda pendente.");
            await Task.Delay(200);
        }
    }

    [Fact]
    public async Task Gera_o_pacote_com_pecas_fotos_pedidos_e_clientes_sem_segredos_e_registra_o_download()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        await api.ConfigureShippingAsync(owner, tenant, "01310-100", pickup: true, "Balcão da loja");
        var part = await api.CreatePartAsync(owner, quantity: 3);
        using (var upload = new HttpRequestMessage(HttpMethod.Post, $"/api/painel/pecas/{part}/fotos") { Content = new ByteArrayContent(TestImages.Jpeg(800, 600)) })
        {
            upload.Headers.Authorization = new("Bearer", owner);
            Assert.True((await api.PanelClient().SendAsync(upload)).IsSuccessStatusCode);
        }
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var placed = await api.StoreClient(tenant).PostAsJsonAsync("/api/loja/pedidos", new
        {
            token, items = new[] { new { partId = part, quantity = 1 } }, expectedTotal = 100m,
            buyer = TestBuyers.Buyer() with { Name = "Joana; \"Exportada\"", Email = "joana@exemplo.com.br" },
            delivery = new { method = "pickup" },
        });
        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        var secret = "segredo-que-nao-pode-sair-" + Guid.NewGuid().ToString("N");
        await InTenantAsync(tenant.Id, sp => sp.GetRequiredService<ISecretVault>().SetAsync(SecretKind.ApiKey, "teste", Encoding.UTF8.GetBytes(secret)));

        var requested = await SendAsync(api.PanelClient(), owner, HttpMethod.Post, "/api/painel/privacidade/exportacoes");
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        var export = await ReadyAsync(owner, (await requested.Content.ReadFromJsonAsync<ExportDto>())!.Id);
        Assert.Equal(("ready", true), (export.Status, export.CanDownload));
        Assert.InRange(export.ExpiresAt!.Value, DateTimeOffset.UtcNow.AddDays(7).AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(7));

        var download = await GetAsync(api.PanelClient(), owner, $"/api/painel/privacidade/exportacoes/{export.Id}/arquivo");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.StartsWith($"exportacao-{tenant.Slug}-", download.Content.Headers.ContentDisposition!.FileNameStar ?? download.Content.Headers.ContentDisposition.FileName, StringComparison.Ordinal);
        var bytes = await download.Content.ReadAsByteArrayAsync();
        using var zip = new ZipArchive(new MemoryStream(bytes));
        string Read(string name)
        {
            using var reader = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        }

        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("LEIAME.txt", names);
        Assert.Contains("clientes.csv", names);
        var photoCount = await InTenantAsync(tenant.Id, sp => sp.GetRequiredService<TenantDbContext>().PartPhotos.CountAsync());
        Assert.Equal(photoCount, names.Count(n => n.StartsWith("fotos/", StringComparison.Ordinal) && n.EndsWith(".jpg", StringComparison.Ordinal)));
        Assert.True(photoCount >= 1);
        Assert.Contains("100,00", Read("pecas.csv"), StringComparison.Ordinal);
        var orders = Read("pedidos.csv");
        Assert.StartsWith("numero;origem;situacao", orders, StringComparison.Ordinal);
        Assert.Contains("\"Joana; \"\"Exportada\"\"\"", orders, StringComparison.Ordinal);
        Assert.Contains("Aguardando pagamento", orders, StringComparison.Ordinal);
        Assert.Contains("joana@exemplo.com.br", Read("clientes.csv"), StringComparison.Ordinal);
        foreach (var entry in zip.Entries)
        {
            using var content = new MemoryStream();
            await using (var stream = entry.Open()) await stream.CopyToAsync(content);
            Assert.DoesNotContain(secret, Encoding.UTF8.GetString(content.ToArray()), StringComparison.Ordinal);
        }

        var log = (await (await GetAsync(api.PanelClient(), owner, "/api/painel/privacidade/acessos")).Content.ReadFromJsonAsync<AccessPageDto>())!;
        Assert.Equal([PersonalDataActions.ExportDownloaded, PersonalDataActions.ExportRequested],
            log.Items.Where(i => i.Action.StartsWith("exportacao", StringComparison.Ordinal)).Select(i => i.Action).ToList());
    }

    [Fact]
    public async Task Uma_por_vez_so_o_Dono_e_vencida_nao_baixa_e_o_arquivo_some()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var operador = await api.SessionForAsync(tenant, TenantRole.Operator);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(api.PanelClient(), operador, HttpMethod.Post, "/api/painel/privacidade/exportacoes")).StatusCode);

        // Uma pendente (o índice único decide): o segundo pedido é recusado.
        await InTenantAsync(tenant.Id, async sp =>
        {
            var db = sp.GetRequiredService<TenantDbContext>();
            db.DataExports.Add(new DataExport(tenant.Id, Guid.NewGuid(), "outro@pecas.test", DateTimeOffset.UtcNow));
            return await db.SaveChangesAsync();
        });
        var refused = await SendAsync(api.PanelClient(), owner, HttpMethod.Post, "/api/painel/privacidade/exportacoes");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("Já existe uma exportação sendo gerada. Aguarde ela ficar pronta.", await TitleAsync(refused));

        // Esquecida há mais de 2 h: vira falha e libera.
        await InTenantAsync(tenant.Id, sp => sp.GetRequiredService<TenantDbContext>().Database
            .ExecuteSqlAsync($"UPDATE data_exports SET requested_at = now() - interval '3 hours' WHERE status = 'Pending'"));
        var requested = await SendAsync(api.PanelClient(), owner, HttpMethod.Post, "/api/painel/privacidade/exportacoes");
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        var export = await ReadyAsync(owner, (await requested.Content.ReadFromJsonAsync<ExportDto>())!.Id);
        Assert.Equal("ready", export.Status);

        await InTenantAsync(tenant.Id, sp => sp.GetRequiredService<TenantDbContext>().Database
            .ExecuteSqlAsync($"UPDATE data_exports SET expires_at = now() - interval '1 minute' WHERE id = {export.Id}"));
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(api.PanelClient(), owner, $"/api/painel/privacidade/exportacoes/{export.Id}/arquivo")).StatusCode);

        await ActivatorUtilities.CreateInstance<IntegrationLogRetention>(api.Services).PurgeAsync(CancellationToken.None);
        var after = (await (await GetAsync(api.PanelClient(), owner, "/api/painel/privacidade/exportacoes")).Content.ReadFromJsonAsync<List<ExportDto>>())!;
        Assert.Equal("expired", after.Single(e => e.Id == export.Id).Status);
        Assert.Null(await api.Services.GetRequiredService<IFileStorage>().GetAsync(StorageArea.Private, DataExport.Key(tenant.Id, export.Id)));
    }

    [Fact]
    public async Task Outra_loja_nao_ve_nem_baixa_a_exportacao()
    {
        var tenant = await api.CreateTenantAsync();
        var other = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var otherOwner = await api.SessionForAsync(other);
        var requested = await SendAsync(api.PanelClient(), owner, HttpMethod.Post, "/api/painel/privacidade/exportacoes");
        var export = await ReadyAsync(owner, (await requested.Content.ReadFromJsonAsync<ExportDto>())!.Id);

        var list = (await (await GetAsync(api.PanelClient(), otherOwner, "/api/painel/privacidade/exportacoes")).Content.ReadFromJsonAsync<List<ExportDto>>())!;
        Assert.Empty(list);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(api.PanelClient(), otherOwner, $"/api/painel/privacidade/exportacoes/{export.Id}/arquivo")).StatusCode);
        // Só RLS, sem o filtro do EF: continua sem ver.
        Assert.Equal(0, await InTenantAsync(other.Id, sp => sp.GetRequiredService<TenantDbContext>().DataExports.IgnoreQueryFilters().CountAsync()));
    }

    [Theory]
    [InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
    [InlineData("+55 48", "'+55 48")]
    [InlineData("@soma", "'@soma")]
    [InlineData("Rua A; 10", "\"Rua A; 10\"")]
    [InlineData("linha 1\nlinha 2", "\"linha 1\nlinha 2\"")]
    [InlineData("Peça comum", "Peça comum")]
    public void Celula_do_CSV_escapa_separador_e_neutraliza_formula(string value, string expected) =>
        Assert.Equal(expected, DataExportBuilder.Cell(value));

    [Fact]
    public void Celula_do_CSV_formata_numero_data_e_sim_nao_em_portugues()
    {
        Assert.Equal("1234,50", DataExportBuilder.Cell(1234.5m));
        Assert.Equal("15/10/2026 09:30:00", DataExportBuilder.Cell(new DateTimeOffset(2026, 10, 15, 12, 30, 0, TimeSpan.Zero)));
        Assert.Equal(("sim", "não", "-3"), (DataExportBuilder.Cell(true), DataExportBuilder.Cell(false), DataExportBuilder.Cell(-3)));
    }
}
