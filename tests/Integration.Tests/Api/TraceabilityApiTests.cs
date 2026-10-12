using System.Net;
using System.Net.Http.Json;
using Ecommerce.Domain.Identity;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>
/// RF10 — rastreabilidade de peça usada: veículos doadores, origem da peça, modo desmontadora (Lei 12.977) travando a
/// venda sem os dados exigidos, e nada disso aparecendo na vitrine (CA3) nem para outra loja.
/// </summary>
public sealed class TraceabilityApiTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record DonorDto(Guid Id, string Label, string? Plate, string? Chassis, string? Renavam, List<string> MissingForDismantler, int PartCount,
        List<DonorPartDto> Parts);
    private sealed record DonorPartDto(Guid Id, string InternalCode, string? TraceabilityTag);
    private sealed record FieldDto(string Key, string Label, bool Required);
    private sealed record SettingsDto(bool DismantlerMode, List<FieldDto> Fields);
    private sealed record TraceDto(Guid? DonorVehicleId, string? DonorVehicleLabel, string? Tag, Dictionary<string, string> CustomFields, List<string> MissingForSale);
    private sealed record PartDto(Guid Id, string InternalCode, string Status, TraceDto Traceability);
    private sealed record PageDto(List<PartDto> Items, int Total);

    private const string Invoice = "35261008330099110001065500100000123410000129";

    private static object Vehicle(string plate = "ABC1D23", string? chassis = "9BWAB45U0BT123456", string? renavam = "00639884962", string? certificate = "CB-123") => new
    {
        plate, chassis, renavam, brand = "Volkswagen", model = "Gol", modelYear = 2011, color = "Prata",
        deregistrationCertificate = certificate, entryInvoiceKey = Invoice, entryDate = "2026-10-01",
    };

    private Task<HttpResponseMessage> Send(string session, HttpMethod method, string path, object? body = null) =>
        SendAsync(api.PanelClient(), session, method, path, body);

    private async Task<DonorDto> CreateDonorAsync(string session, object vehicle)
    {
        var response = await Send(session, HttpMethod.Post, "/api/painel/doadores", vehicle);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DonorDto>())!;
    }

    private Task<HttpResponseMessage> TraceAsync(string session, Guid part, Guid? donor, string? tag, Dictionary<string, string?>? fields = null) =>
        Send(session, HttpMethod.Put, $"/api/painel/pecas/{part}/rastreabilidade", new { donorVehicleId = donor, tag, customFields = fields });

    private async Task AddPhotoAsync(string session, Guid part)
    {
        using var photo = new HttpRequestMessage(HttpMethod.Post, $"/api/painel/pecas/{part}/fotos") { Content = new ByteArrayContent(Infrastructure.TestImages.Jpeg(120, 90)) };
        photo.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session);
        Assert.Equal(HttpStatusCode.Created, (await api.PanelClient().SendAsync(photo)).StatusCode);
    }

    private Task<HttpResponseMessage> ActivateAsync(string session, Guid part) =>
        Send(session, HttpMethod.Put, $"/api/painel/pecas/{part}/situacao", new { status = "active" });

    [Fact]
    public async Task Cadastro_de_doador_valida_e_nao_repete_placa_chassi_ou_renavam()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);

        var donor = await CreateDonorAsync(owner, Vehicle());
        Assert.Equal(("Volkswagen Gol 2011 · ABC1D23", "00639884962"), (donor.Label, donor.Renavam));
        Assert.Empty(donor.MissingForDismantler);

        Assert.Equal(HttpStatusCode.Conflict, (await Send(owner, HttpMethod.Post, "/api/painel/doadores", Vehicle(chassis: null, renavam: null))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(owner, HttpMethod.Post, "/api/painel/doadores", Vehicle(plate: "XYZ9999", renavam: null))).StatusCode);
        var invalid = await Send(owner, HttpMethod.Post, "/api/painel/doadores", Vehicle(plate: "XYZ9999", chassis: null, renavam: "00639884963"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("Renavam", await TitleAsync(invalid), StringComparison.Ordinal);

        var second = await CreateDonorAsync(owner, Vehicle(plate: "XYZ9999", chassis: null, renavam: null, certificate: null));
        Assert.Equal(["chassi", "Renavam", "certidão de baixa"], second.MissingForDismantler);
        var page = await (await GetAsync(api.PanelClient(), owner, "/api/painel/doadores?busca=xyz-9999")).Content.ReadFromJsonAsync<PageOf<DonorDto>>();
        Assert.Equal(second.Id, Assert.Single(page!.Items).Id);
    }

    private sealed record PageOf<T>(List<T> Items, int Total);

    [Fact]
    public async Task Origem_da_peca_com_etiqueta_unica_campos_configurados_e_busca_pela_etiqueta()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var settings = await (await Send(owner, HttpMethod.Put, "/api/painel/rastreabilidade",
            new { dismantlerMode = false, fields = new[] { new { key = "", label = "Registro Detran", required = true } } })).Content.ReadFromJsonAsync<SettingsDto>();
        var key = Assert.Single(settings!.Fields).Key;
        var donor = await CreateDonorAsync(owner, Vehicle());
        var part = await api.CreatePartAsync(owner, active: false);
        var other = await api.CreatePartAsync(owner, active: false);

        var traced = await TraceAsync(owner, part, donor.Id, "et-777", new() { [key] = "SP-2026-1" });
        Assert.Equal(HttpStatusCode.OK, traced.StatusCode);
        var view = (await traced.Content.ReadFromJsonAsync<PartDto>())!.Traceability;
        Assert.Equal((donor.Id, "ET-777", "SP-2026-1"), (view.DonorVehicleId, view.Tag, view.CustomFields[key]));

        Assert.Equal(HttpStatusCode.Conflict, (await TraceAsync(owner, other, null, "ET-777")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await TraceAsync(owner, other, null, null, new() { ["inexistente"] = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await TraceAsync(owner, other, Guid.CreateVersion7(), null)).StatusCode);

        var found = await (await GetAsync(api.PanelClient(), owner, "/api/painel/pecas?busca=et-777")).Content.ReadFromJsonAsync<PageDto>();
        Assert.Equal(part, Assert.Single(found!.Items).Id);
        var withParts = await (await GetAsync(api.PanelClient(), owner, $"/api/painel/doadores/{donor.Id}")).Content.ReadFromJsonAsync<DonorDto>();
        Assert.Equal(("ET-777", 1), (Assert.Single(withParts!.Parts).TraceabilityTag, withParts.PartCount));

        // Veículo com peças não é excluído; sem peças, sim.
        Assert.Equal(HttpStatusCode.Conflict, (await Send(owner, HttpMethod.Delete, $"/api/painel/doadores/{donor.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TraceAsync(owner, part, null, "ET-777")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(owner, HttpMethod.Delete, $"/api/painel/doadores/{donor.Id}")).StatusCode);
    }

    [Fact]
    public async Task Modo_desmontadora_so_poe_peca_usada_a_venda_com_a_origem_completa()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var settings = await (await Send(owner, HttpMethod.Put, "/api/painel/rastreabilidade",
            new { dismantlerMode = true, fields = new[] { new { key = "", label = "Registro Detran", required = true } } })).Content.ReadFromJsonAsync<SettingsDto>();
        var key = settings!.Fields[0].Key;
        var part = await api.CreatePartAsync(owner, active: false);
        await AddPhotoAsync(owner, part);

        var blocked = await ActivateAsync(owner, part);
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        var message = await TitleAsync(blocked);
        Assert.Contains("veículo doador", message, StringComparison.Ordinal);
        Assert.Contains("etiqueta de rastreabilidade", message, StringComparison.Ordinal);
        Assert.Contains("Registro Detran", message, StringComparison.Ordinal);

        var incomplete = await CreateDonorAsync(owner, Vehicle(certificate: null));
        await TraceAsync(owner, part, incomplete.Id, "ET-1", new() { [key] = "123" });
        Assert.Contains("certidão de baixa", await TitleAsync(await ActivateAsync(owner, part)), StringComparison.Ordinal);

        var update = await Send(owner, HttpMethod.Put, $"/api/painel/doadores/{incomplete.Id}", Vehicle());
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ActivateAsync(owner, part)).StatusCode);

        // À venda, a origem não pode ficar incompleta.
        Assert.Equal(HttpStatusCode.BadRequest, (await TraceAsync(owner, part, incomplete.Id, null, new() { [key] = "123" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(owner, HttpMethod.Put, $"/api/painel/doadores/{incomplete.Id}", Vehicle(certificate: null))).StatusCode);
        var view = (await (await GetAsync(api.PanelClient(), owner, $"/api/painel/pecas/{part}")).Content.ReadFromJsonAsync<PartDto>())!;
        Assert.Equal(("active", "ET-1"), (view.Status, view.Traceability.Tag));
        Assert.Empty(view.Traceability.MissingForSale);

        // Peça nova não precisa de origem.
        var created = await Send(owner, HttpMethod.Post, "/api/painel/pecas", new
        {
            internalCode = "NOVA-1", title = "Farol novo", condition = "new", price = 300m, quantity = 1, oemCodes = Array.Empty<string>(),
        });
        var fresh = (await created.Content.ReadFromJsonAsync<PartDto>())!.Id;
        await AddPhotoAsync(owner, fresh);
        Assert.Equal(HttpStatusCode.OK, (await ActivateAsync(owner, fresh)).StatusCode);
    }

    [Fact]
    public async Task Dados_do_doador_e_da_etiqueta_nunca_aparecem_na_vitrine()
    {
        var tenant = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(tenant);
        var settings = await (await Send(owner, HttpMethod.Put, "/api/painel/rastreabilidade",
            new { dismantlerMode = false, fields = new[] { new { key = "", label = "Registro Detran", required = false } } })).Content.ReadFromJsonAsync<SettingsDto>();
        var donor = await CreateDonorAsync(owner, Vehicle());
        var part = await api.CreatePartAsync(owner, title: "Farol de teste");
        Assert.Equal(HttpStatusCode.OK, (await TraceAsync(owner, part, donor.Id, "ETQ-SEGREDO-9", new() { [settings!.Fields[0].Key] = "REGISTRO-SEGREDO" })).StatusCode);

        var store = api.StoreClient(tenant, "198.51.104.10");
        var detail = await store.GetStringAsync($"/api/loja/pecas/{part}");
        var search = await store.GetStringAsync("/api/loja/pecas?busca=Farol");
        Assert.Contains("Farol de teste", detail, StringComparison.Ordinal);
        foreach (var secret in new[] { "ABC1D23", "9BWAB45U0BT123456", "00639884962", "ETQ-SEGREDO-9", "REGISTRO-SEGREDO", Invoice, "CB-123", donor.Id.ToString() })
        {
            Assert.DoesNotContain(secret, detail, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, search, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain("Farol de teste", await store.GetStringAsync("/api/loja/pecas?busca=ETQ-SEGREDO-9"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Outra_loja_nao_ve_doador_e_operador_nao_muda_a_configuracao()
    {
        var mine = await api.CreateTenantAsync();
        var other = await api.CreateTenantAsync();
        var owner = await api.SessionForAsync(mine);
        var operatorSession = await api.SessionForAsync(mine, TenantRole.Operator);
        var stranger = await api.SessionForAsync(other);
        var donor = await CreateDonorAsync(owner, Vehicle());

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(api.PanelClient(), stranger, $"/api/painel/doadores/{donor.Id}")).StatusCode);
        Assert.Empty((await (await GetAsync(api.PanelClient(), stranger, "/api/painel/doadores")).Content.ReadFromJsonAsync<PageOf<DonorDto>>())!.Items);
        var strangerPart = await api.CreatePartAsync(stranger, active: false);
        Assert.Equal(HttpStatusCode.BadRequest, (await TraceAsync(stranger, strangerPart, donor.Id, null)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(api.PanelClient(), operatorSession, "/api/painel/rastreabilidade")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send(operatorSession, HttpMethod.Put, "/api/painel/rastreabilidade", new { dismantlerMode = true, fields = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(operatorSession, HttpMethod.Post, "/api/painel/doadores", Vehicle(plate: "OPR1234", chassis: null, renavam: null))).StatusCode);
    }
}
