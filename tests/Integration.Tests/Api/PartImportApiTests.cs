using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Inventory;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF11 — importação de peças por planilha: prévia com relatório, confirmação, cadastro em lotes pela fila com progresso,
/// códigos já cadastrados pulados, reenvio sem duplicar e isolamento entre lojas.
/// </summary>
public sealed class PartImportApiTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record IssueDto(int Line, string Message);
    private sealed record SkipDto(int Line, string InternalCode);
    private sealed record ImportDto(Guid Id, string FileName, string Status, int TotalRows, int ValidRows, int ErrorCount, int ExistingCodesAtPreview,
        int ProcessedRows, int ImportedCount, int SkippedCount, List<IssueDto> Errors, List<SkipDto> Skipped);
    private sealed record StockDto(int OnHand, int Reserved, int Available);
    private sealed record PartDto(Guid Id, string InternalCode, string Title, string Status, decimal Price, StockDto Stock, List<string> OemCodes);
    private sealed record PageDto(List<PartDto> Items, int Total);

    private const string Header = "codigo;titulo;descricao;estado;preco;quantidade;comprimento_cm;largura_cm;altura_cm;peso_g;oem\n";

    private async Task<HttpResponseMessage> UploadAsync(string session, byte[] content, string fileName = "pecas.csv")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/painel/pecas/importacoes?arquivo={Uri.EscapeDataString(fileName)}")
        {
            Content = new ByteArrayContent(content),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);
        return await api.PanelClient().SendAsync(request);
    }

    private async Task<ImportDto> PreviewAsync(string session, string csv)
    {
        var response = await UploadAsync(session, Encoding.UTF8.GetBytes(csv));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ImportDto>())!;
    }

    private async Task<ImportDto> ConfirmAndWaitAsync(string session, Guid id)
    {
        var confirmed = await SendAsync(api.PanelClient(), session, HttpMethod.Post, $"/api/painel/pecas/importacoes/{id}/confirmar");
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var view = (await (await GetAsync(api.PanelClient(), session, $"/api/painel/pecas/importacoes/{id}")).Content.ReadFromJsonAsync<ImportDto>())!;
            if (view.Status == "completed") return view;
            if (watch.Elapsed > TimeSpan.FromSeconds(60)) Assert.Fail($"Importação ainda {view.Status} ({view.ProcessedRows}/{view.ValidRows}).");
            await Task.Delay(150);
        }
    }

    private async Task<PartDto?> PartAsync(string session, string code) =>
        (await (await GetAsync(api.PanelClient(), session, $"/api/painel/pecas?busca={Uri.EscapeDataString(code)}")).Content.ReadFromJsonAsync<PageDto>())!
            .Items.SingleOrDefault(p => p.InternalCode == code);

    private async Task<T> InTenantAsync<T>(Guid tenantId, Func<TenantDbContext, Task<T>> query)
    {
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        return await query(scope.ServiceProvider.GetRequiredService<TenantDbContext>());
    }

    [Fact]
    public async Task Previa_mostra_erros_e_so_a_confirmacao_cadastra_as_pecas_como_rascunho()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var preview = await PreviewAsync(owner, Header
            + "IMP-1;Farol esquerdo;Lente boa;usada;350,00;2;45;30;25;3500;5U0941015 | 5U0941015A\n"
            + "IMP-2;Retrovisor;;nova;180,5;0;;;;;\n"
            + "IMP-3;;;usada;10;1;;;;;\n");

        Assert.Equal(("pecas.csv", "validated", 3, 2, 1), (preview.FileName, preview.Status, preview.TotalRows, preview.ValidRows, preview.ErrorCount));
        Assert.Equal(4, Assert.Single(preview.Errors).Line);
        Assert.Null(await PartAsync(owner, "IMP-1"));

        var done = await ConfirmAndWaitAsync(owner, preview.Id);

        Assert.Equal((2, 2, 0), (done.ProcessedRows, done.ImportedCount, done.SkippedCount));
        var farol = (await PartAsync(owner, "IMP-1"))!;
        Assert.Equal(("draft", 350m, 2), (farol.Status, farol.Price, farol.Stock.OnHand));
        Assert.Equal(0, (await PartAsync(owner, "IMP-2"))!.Stock.OnHand);
        var movement = await InTenantAsync(tenant.Id, db => db.StockMovements.SingleAsync(m => m.PartId == farol.Id));
        Assert.Equal((2, StockMovementReason.Adjustment, "importação pecas.csv"), (movement.Delta, movement.Reason, movement.Reference));
        Assert.NotNull(movement.UserId);
        Assert.Equal(2, await InTenantAsync(tenant.Id, db => db.PartOemCodes.CountAsync(o => o.PartId == farol.Id)));
    }

    [Fact]
    public async Task Codigo_ja_cadastrado_e_pulado_e_reenviar_a_mesma_planilha_nao_duplica()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var manual = await api.CreatePartAsync(owner, price: 99m, quantity: 7, active: false);
        var manualCode = (await (await GetAsync(api.PanelClient(), owner, $"/api/painel/pecas/{manual}")).Content.ReadFromJsonAsync<PartDto>())!.InternalCode;
        var csv = Header + $"{manualCode};Outro título;;usada;10;1;;;;;\nIMP-9;Lanterna;;usada;80;1;;;;;\n";

        var first = await PreviewAsync(owner, csv);
        Assert.Equal(1, first.ExistingCodesAtPreview);
        var done = await ConfirmAndWaitAsync(owner, first.Id);
        Assert.Equal((1, 1), (done.ImportedCount, done.SkippedCount));
        Assert.Equal((2, manualCode), (done.Skipped[0].Line, done.Skipped[0].InternalCode));
        var untouched = (await PartAsync(owner, manualCode))!;
        Assert.Equal((99m, 7), (untouched.Price, untouched.Stock.OnHand));

        var again = await ConfirmAndWaitAsync(owner, (await PreviewAsync(owner, csv)).Id);
        Assert.Equal((0, 2), (again.ImportedCount, again.SkippedCount));
        Assert.Equal(2, await InTenantAsync(tenant.Id, db => db.Parts.CountAsync()));
    }

    [Fact]
    public async Task Codigo_em_branco_na_planilha_e_gerado_sem_tomar_o_de_outra_linha()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var preview = await PreviewAsync(owner, Header
            + ";Farol;;usada;100;1;;;;;\n"
            + ";Lanterna;;usada;80;1;;;;;\n"
            + "P-000002;Retrovisor;;usada;90;1;;;;;\n");
        Assert.Equal((3, 0), (preview.ValidRows, preview.ErrorCount));

        var done = await ConfirmAndWaitAsync(owner, preview.Id);

        Assert.Equal((3, 0), (done.ImportedCount, done.SkippedCount));
        Assert.Equal("Farol", (await PartAsync(owner, "P-000001"))!.Title);
        Assert.Equal("Lanterna", (await PartAsync(owner, "P-000003"))!.Title); // P-000002 é da 3ª linha
        Assert.Equal("Retrovisor", (await PartAsync(owner, "P-000002"))!.Title);
    }

    [Fact]
    public async Task Planilha_grande_e_cadastrada_em_lotes_e_confirmar_duas_vezes_nao_repete()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var csv = new StringBuilder(Header);
        for (var i = 1; i <= 250; i++) csv.Append(CultureInfo.InvariantCulture, $"L-{i:D4};Peça {i};;usada;{i},90;1;;;;;\n");
        var preview = await PreviewAsync(owner, csv.ToString());

        var confirms = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            SendAsync(api.PanelClient(), owner, HttpMethod.Post, $"/api/painel/pecas/importacoes/{preview.Id}/confirmar")));
        Assert.All(confirms, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var done = await ConfirmAndWaitAsync(owner, preview.Id);

        Assert.Equal((250, 250), (done.ProcessedRows, done.ImportedCount));
        Assert.Equal(250, await InTenantAsync(tenant.Id, db => db.Parts.CountAsync()));
        Assert.Equal(250, await InTenantAsync(tenant.Id, db => db.Stocks.SumAsync(s => s.OnHand)));
    }

    [Fact]
    public async Task Arquivo_invalido_e_recusado_sem_gravar_importacao()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);

        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(owner, [])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(owner, [.. "PK\u0003\u0004"u8, 9, 9, 9], "x.xlsx")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(owner, Encoding.UTF8.GetBytes("codigo;titulo\nK;X\n"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(owner, new byte[5 * 1024 * 1024 + 1])).StatusCode);
        Assert.Equal(0, await InTenantAsync(tenant.Id, db => db.PartImports.CountAsync()));
    }

    [Fact]
    public async Task Modelo_baixado_e_importado_de_volta()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);

        var template = await GetAsync(api.PanelClient(), owner, "/api/painel/pecas/importacoes/modelo?formato=xlsx");
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", template.Content.Headers.ContentType!.MediaType);
        var bytes = await template.Content.ReadAsByteArrayAsync();
        var preview = (await (await UploadAsync(owner, bytes, "modelo.xlsx")).Content.ReadFromJsonAsync<ImportDto>())!;
        Assert.Equal((2, 0), (preview.ValidRows, preview.ErrorCount));

        var csv = await GetAsync(api.PanelClient(), owner, "/api/painel/pecas/importacoes/modelo?formato=csv");
        Assert.StartsWith("codigo;titulo;", Encoding.UTF8.GetString(await csv.Content.ReadAsByteArrayAsync()).TrimStart('﻿'), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(api.PanelClient(), owner, "/api/painel/pecas/importacoes/modelo?formato=pdf")).StatusCode);
    }

    [Fact]
    public async Task Importacao_de_uma_loja_nao_e_vista_nem_confirmada_por_outra()
    {
        var mine = await api.CreateTenantAsync();
        var other = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(mine);
        var stranger = await api.SessionForAsync(other, TenantRole.Operator);
        var preview = await PreviewAsync(owner, Header + "ISO-1;Farol;;usada;10;1;;;;;\n");

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(api.PanelClient(), stranger, $"/api/painel/pecas/importacoes/{preview.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(api.PanelClient(), stranger, HttpMethod.Post, $"/api/painel/pecas/importacoes/{preview.Id}/confirmar")).StatusCode);
        Assert.Empty((await (await GetAsync(api.PanelClient(), stranger, "/api/painel/pecas/importacoes")).Content.ReadFromJsonAsync<List<ImportDto>>())!);
        Assert.Equal("validated", (await InTenantAsync(mine.Id, db => db.PartImports.SingleAsync())).Status.ToString().ToLowerInvariant());
    }
}
