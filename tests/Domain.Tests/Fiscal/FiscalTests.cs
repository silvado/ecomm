using System.Globalization;
using Ecommerce.Domain.Fiscal;
using Ecommerce.Domain.Orders;

namespace Ecommerce.Domain.Tests.Fiscal;

/// <summary>RF19: códigos fiscais, chave de acesso (inclusive com CNPJ alfanumérico) e ciclo da NF-e.</summary>
public sealed class FiscalTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Order = Guid.CreateVersion7();

    public static string Key(string cnpj = "11222333000181", int number = 1234, string model = "55")
    {
        var body = string.Create(CultureInfo.InvariantCulture, $"352610{cnpj}{model}001{number:D9}112345678");
        return body + AccessKey.CheckDigit(body).ToString(CultureInfo.InvariantCulture);
    }

    private static FiscalRecipient Recipient() =>
        new("Maria da Silva", "529.982.247-25", "Maria@Exemplo.com.br", new DeliveryAddress("88015-200", "Rua Felipe Schmidt", "100", null, "Centro", "Florianópolis", "sc"));

    [Fact]
    public void Chave_valida_com_espacos_e_normalizada()
    {
        var key = Key();
        var spaced = string.Join(' ', Enumerable.Range(0, 11).Select(i => key.Substring(i * 4, 4)));
        Assert.Equal(key, AccessKey.Normalize(spaced));
        Assert.Equal("11222333000181", AccessKey.IssuerCnpj(key));
    }

    [Fact]
    public void Chave_com_cnpj_alfanumerico_usa_ascii_menos_48()
    {
        var key = Key("12ABC34501DE35");
        Assert.Equal(key, AccessKey.Normalize(key.ToLowerInvariant()));
        Assert.Equal("12ABC34501DE35", AccessKey.IssuerCnpj(key));
    }

    [Fact]
    public void Chave_com_digito_errado_modelo_errado_ou_tamanho_errado_e_recusada()
    {
        var key = Key();
        var wrongDigit = key[..43] + ((key[43] - '0' + 1) % 10).ToString(CultureInfo.InvariantCulture);
        Assert.Throws<ArgumentException>(() => AccessKey.Normalize(wrongDigit));
        Assert.Throws<ArgumentException>(() => AccessKey.Normalize(Key(model: "65")));
        Assert.Throws<ArgumentException>(() => AccessKey.Normalize(key[..40]));
        Assert.Throws<ArgumentException>(() => AccessKey.Normalize(null));
    }

    [Theory]
    [InlineData("8708.29.99", "87082999")]
    [InlineData(" 87082999 ", "87082999")]
    [InlineData("", null)]
    public void Ncm_normalizado(string input, string? expected) => Assert.Equal(expected, FiscalRules.NormalizeNcm(input));

    [Theory]
    [InlineData("870829")]
    [InlineData("8708299A")]
    public void Ncm_invalido(string input) => Assert.Throws<ArgumentException>(() => FiscalRules.NormalizeNcm(input));

    [Fact]
    public void Csosn_e_cst_dependem_do_regime()
    {
        Assert.Equal("102", FiscalRules.NormalizeTaxCode("102", TaxRegime.SimplesNacional));
        Assert.Equal("00", FiscalRules.NormalizeTaxCode("00", TaxRegime.Normal));
        Assert.Throws<ArgumentException>(() => FiscalRules.NormalizeTaxCode("00", TaxRegime.SimplesNacional));
        Assert.Throws<ArgumentException>(() => FiscalRules.NormalizeTaxCode("102", TaxRegime.Normal));
    }

    [Fact]
    public void Configuracao_pelo_provedor_exige_o_minimo_para_montar_a_nota()
    {
        var incomplete = FiscalSettings.Default with { Mode = FiscalMode.Provider };
        Assert.Throws<ArgumentException>(() => FiscalRules.Normalize(incomplete));

        var ok = FiscalRules.Normalize(incomplete with { State = "sc", DefaultNcm = "8708.29.99" });
        Assert.Equal(("SC", "87082999", "5102", "6102"), (ok.State, ok.DefaultNcm, ok.CfopInState, ok.CfopOutOfState));
        Assert.Throws<ArgumentException>(() => FiscalRules.Normalize(ok with { CfopOutOfState = "5102" }));
        Assert.Throws<ArgumentException>(() => FiscalRules.Normalize(ok with { State = "XX" }));
        Assert.Throws<ArgumentException>(() => FiscalRules.Normalize(ok with { DefaultOrigin = 9 }));
    }

    [Fact]
    public void Destinatario_exige_documento_e_endereco()
    {
        var normalized = FiscalRecipient.Normalize(Recipient());
        Assert.Equal(("52998224725", "maria@exemplo.com.br", "SC"), (normalized.Document, normalized.Email, normalized.Address!.State));
        Assert.Throws<ArgumentException>(() => FiscalRecipient.Normalize(Recipient() with { Address = null }));
        Assert.Throws<ArgumentException>(() => FiscalRecipient.Normalize(Recipient() with { Document = "123" }));
        Assert.Equal("12ABC34501DE35", FiscalRecipient.Normalize(Recipient() with { Document = "12.ABC.345/01DE-35" }).Document);
    }

    private static FiscalDocument Processing() =>
        FiscalDocument.Request(Tenant, Order, "PED10-1", "nfe-simulada", FiscalEnvironment.Homologation, Recipient(), Guid.NewGuid(), Now);

    [Fact]
    public void Autorizada_pode_ser_cancelada_so_em_24_horas_com_justificativa()
    {
        var document = Processing();
        document.Authorize(Key(), 1234, 1, Now);
        Assert.True(document.IsActive);

        Assert.Throws<ArgumentException>(() => document.Cancel("curta", Now));
        Assert.False(document.CanCancel(Now.AddHours(24).AddSeconds(1)));
        Assert.Throws<InvalidOperationException>(() => document.Cancel("Venda desfeita a pedido do cliente", Now.AddHours(25)));

        document.Cancel("  Venda   desfeita a pedido do cliente ", Now.AddHours(23));
        Assert.Equal((FiscalDocumentStatus.Canceled, "Venda desfeita a pedido do cliente"), (document.Status, document.CancelJustification));
        Assert.False(document.IsActive);
    }

    [Fact]
    public void Rejeitada_guarda_o_motivo_e_nao_pode_ser_autorizada_depois()
    {
        var document = Processing();
        document.Reject("Rejeição 778: NCM inexistente", Now);
        Assert.Equal(FiscalDocumentStatus.Rejected, document.Status);
        Assert.False(document.IsActive);
        Assert.Throws<InvalidOperationException>(() => document.Authorize(Key(), 1, 1, Now));
        Assert.False(document.CanCancel(Now));
    }

    [Fact]
    public void Externa_nasce_autorizada_com_numero_e_serie_da_chave_e_pode_ser_retirada_a_qualquer_tempo()
    {
        var document = FiscalDocument.RegisterExternal(Tenant, Order, Key(number: 987), null, null, Now);
        Assert.Equal((FiscalDocumentStatus.Authorized, 987, 1), (document.Status, document.Number, document.Series));
        Assert.True(document.CanCancel(Now.AddDays(30)));
    }
}
