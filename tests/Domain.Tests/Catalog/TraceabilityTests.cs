using Ecommerce.Domain.Catalog;
using Ecommerce.Domain.Inventory;

namespace Ecommerce.Domain.Tests.Catalog;

/// <summary>Rastreabilidade de peça usada (RF10): veículo doador, etiqueta, campos extras e modo desmontadora.</summary>
public sealed class TraceabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private const string InvoiceKey = "35261008330099110001065500100000123410000129";

    private static DonorVehicleDetails Complete(string plate = "ABC1D23") =>
        new(plate, "9BWAB45U0BT123456", "00639884962", "Volkswagen", "Gol", 2011, "Prata", "CB-2026/000123", InvoiceKey,
            new DateOnly(2026, 10, 1), null);

    private static Part UsedPart() =>
        Part.Create(Guid.CreateVersion7(), "K-1", new PartDetails("Farol", null, PartCondition.Used, 100m, null, null, null, null, []), Now);

    [Fact]
    public void Veiculo_completo_normaliza_placa_chassi_renavam_e_nf()
    {
        var vehicle = DonorVehicle.Create(Guid.CreateVersion7(), Complete() with { Plate = "abc-1d23", Renavam = "639884962", EntryInvoiceKey = "3526 1008 3300 9911 0001 0655 0010 0000 1234 1000 0129" }, Now);

        Assert.Equal(("ABC1D23", "9BWAB45U0BT123456", "00639884962", InvoiceKey), (vehicle.Plate, vehicle.Chassis, vehicle.Renavam, vehicle.EntryInvoiceKey));
        Assert.Equal("Volkswagen Gol 2011 · ABC1D23", vehicle.Label);
        Assert.True(vehicle.IsCompleteForDismantler);
    }

    [Theory]
    [InlineData("ABC1234")]
    [InlineData("ABC1D23")]
    public void Placa_antiga_e_mercosul_sao_aceitas(string plate) =>
        Assert.Equal(plate, DonorVehicle.NormalizePlate(plate));

    [Theory]
    [InlineData("AB1234")]
    [InlineData("1BC1D23")]
    [InlineData("ABCD123")]
    public void Placa_fora_do_formato_e_recusada(string plate) =>
        Assert.Throws<ArgumentException>(() => DonorVehicle.NormalizePlate(plate));

    [Theory]
    [InlineData("9BWAB45U0BT12345")] // 16
    [InlineData("9BWAB45U0BT1234O")] // letra O
    public void Chassi_invalido_e_recusado(string chassis) =>
        Assert.Throws<ArgumentException>(() => DonorVehicle.NormalizeChassis(chassis));

    [Theory]
    [InlineData("00639884963")]
    [InlineData("1234567890")]
    [InlineData("12345678901")]
    public void Renavam_com_digito_errado_e_recusado(string renavam) =>
        Assert.Throws<ArgumentException>(() => DonorVehicle.NormalizeRenavam(renavam));

    [Fact]
    public void Renavam_valido_com_11_digitos()
    {
        Assert.Equal("12345678900", DonorVehicle.NormalizeRenavam("12345678900"));
        Assert.Equal("01234567897", DonorVehicle.NormalizeRenavam("01234567897"));
    }

    [Fact]
    public void Chave_da_nf_com_digito_errado_ou_tamanho_errado_e_recusada()
    {
        Assert.Throws<ArgumentException>(() => DonorVehicle.NormalizeInvoiceKey(InvoiceKey[..43] + "0"));
        Assert.Throws<ArgumentException>(() => DonorVehicle.NormalizeInvoiceKey(InvoiceKey[..40]));
    }

    [Fact]
    public void Marca_modelo_e_placa_ou_chassi_sao_obrigatorios()
    {
        Assert.Throws<ArgumentException>(() => DonorVehicle.Create(Guid.CreateVersion7(), Complete() with { Brand = " " }, Now));
        Assert.Throws<ArgumentException>(() => DonorVehicle.Create(Guid.CreateVersion7(), Complete() with { Plate = null, Chassis = "" }, Now));
        Assert.Throws<ArgumentException>(() => DonorVehicle.Create(Guid.CreateVersion7(), Complete() with { ModelYear = 2028 }, Now));

        var minimal = DonorVehicle.Create(Guid.CreateVersion7(), new DonorVehicleDetails("ABC1234", null, null, "Fiat", "Uno", null, null, null, null, null, null), Now);
        Assert.Equal(["chassi", "Renavam", "certidão de baixa", "NF de entrada"], minimal.MissingForDismantler());
    }

    [Fact]
    public void Fora_do_modo_desmontadora_nada_e_exigido()
    {
        Assert.Empty(Traceability.MissingForSale(false, [new TraceabilityField("detran", "Registro Detran", true)], UsedPart(), null));
    }

    [Fact]
    public void Modo_desmontadora_exige_doador_completo_etiqueta_e_campos_obrigatorios_so_de_peca_usada()
    {
        TraceabilityField[] fields = [new("detran", "Registro Detran", true), new("lote", "Lote", false)];
        var part = UsedPart();
        var incomplete = DonorVehicle.Create(Guid.CreateVersion7(), Complete() with { DeregistrationCertificate = null }, Now);

        Assert.Equal(["veículo doador", "etiqueta de rastreabilidade", "Registro Detran"], Traceability.MissingForSale(true, fields, part, null));
        Assert.Equal("dados do veículo doador (certidão de baixa)", Traceability.MissingForSale(true, fields, part, incomplete)[0]);

        part.SetTraceability(Guid.CreateVersion7(), " et-0001 ", new Dictionary<string, string?> { ["detran"] = " 123 ", ["lote"] = "  " }, Now);
        Assert.Equal(("ET-0001", "123"), (part.TraceabilityTag, part.CustomFields["detran"]));
        Assert.False(part.CustomFields.ContainsKey("lote"));
        Assert.Empty(Traceability.MissingForSale(true, fields, part, DonorVehicle.Create(Guid.CreateVersion7(), Complete(), Now)));

        var newPart = Part.Create(Guid.CreateVersion7(), "K-2", new PartDetails("Farol novo", null, PartCondition.New, 100m, null, null, null, null, []), Now);
        Assert.Empty(Traceability.MissingForSale(true, fields, newPart, null));
    }

    [Fact]
    public void Campos_extras_ganham_chave_e_mantem_a_existente()
    {
        var settings = new StoreSettings(Guid.CreateVersion7());
        settings.ConfigureTraceability(true, [new TraceabilityField("", " Registro  Detran ", true)]);
        var key = Assert.Single(settings.TraceabilityFields).Key;
        Assert.Equal(("Registro Detran", true), (settings.TraceabilityFields[0].Label, settings.DismantlerMode));

        settings.ConfigureTraceability(false, [new TraceabilityField(key, "Registro no Detran", false), new TraceabilityField("", "Lote", false)]);
        Assert.Equal(key, settings.TraceabilityFields[0].Key);
        Assert.Equal(2, settings.TraceabilityFields.Count);

        Assert.Throws<ArgumentException>(() => settings.ConfigureTraceability(true, [new("", "Lote", false), new("", "lote", true)]));
        Assert.Throws<ArgumentException>(() => settings.ConfigureTraceability(true, Enumerable.Range(0, 11).Select(i => new TraceabilityField("", $"Campo {i}", false)).ToList()));
    }

    [Fact]
    public void Etiqueta_longa_e_recusada() =>
        Assert.Throws<ArgumentException>(() => UsedPart().SetTraceability(null, new string('X', Part.TraceabilityTagMaxLength + 1), null, Now));
}
