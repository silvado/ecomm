using Ecommerce.Application.Channels;

namespace Ecommerce.Application.Tests.Channels;

/// <summary>RF11 CA2 — veículos do anúncio do ML viram compatibilidades da peça só quando casam com segurança com a tabela.</summary>
public sealed class ChannelCompatibilityRulesTests
{
    private static readonly Guid GolG5Flex10 = Guid.CreateVersion7();
    private static readonly Guid GolG5Flex16 = Guid.CreateVersion7();
    private static readonly Guid GolG6 = Guid.CreateVersion7();
    private static readonly Guid Ka = Guid.CreateVersion7();
    private static readonly Guid GolModel = Guid.CreateVersion7();
    private static readonly Guid KaModel = Guid.CreateVersion7();

    private static readonly VehicleCandidate[] Table =
    [
        new(GolG5Flex10, GolModel, "Volkswagen", "Gol", "1.0 8V Flex (G5)", 2009, 2012),
        new(GolG5Flex16, GolModel, "Volkswagen", "Gol", "1.6 8V Flex (G5)", 2009, 2012),
        new(GolG6, GolModel, "Volkswagen", "Gol", "1.0 8V Flex (G6)", 2013, null),
        new(Ka, KaModel, "Ford", "Ka", "1.0 Ti-VCT Flex", 2015, 2021),
    ];

    [Fact]
    public void Motor_do_ML_escolhe_a_versao_e_anos_seguidos_viram_uma_faixa()
    {
        var result = ChannelCompatibilityRules.Match(
            [new("VOLKSWAGEN", "Gol", 2010, "1.6 8V"), new("Volkswagen", "Gol", 2011, "1.6 8V"), new("Volkswagen", "Gol", 2012, "1.6 8V")], Table);

        Assert.Equal([new CompatibilityMatch(GolG5Flex16, 2010, 2012)], result.Matches);
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public void Ano_com_buraco_vira_duas_faixas()
    {
        var result = ChannelCompatibilityRules.Match([new("Ford", "Ka", 2015, null), new("Ford", "Ka", 2016, null), new("Ford", "Ka", 2019, null)], Table);
        Assert.Equal([new CompatibilityMatch(Ka, 2015, 2016), new CompatibilityMatch(Ka, 2019, 2019)], result.Matches);
    }

    [Fact]
    public void Sem_motor_so_vale_quando_o_modelo_tem_uma_versao_no_ano()
    {
        var result = ChannelCompatibilityRules.Match([new("Volkswagen", "Gol", 2010, null), new("Volkswagen", "Gol", 2014, null)], Table);
        Assert.Equal([new CompatibilityMatch(GolG6, 2014, 2014)], result.Matches);
        Assert.Equal(["Volkswagen Gol 2010"], result.Unmatched);
    }

    [Fact]
    public void Motor_que_nao_bate_ou_empata_fica_de_fora()
    {
        var result = ChannelCompatibilityRules.Match(
            [new("Volkswagen", "Gol", 2010, "2.0 16V"), new("Volkswagen", "Gol", 2010, "Flex"), new("Volkswagen", "Gol", 2008, "1.0 8V")], Table);
        Assert.Empty(result.Matches);
        Assert.Equal(["Volkswagen Gol 2010 2.0 16V", "Volkswagen Gol 2010 Flex", "Volkswagen Gol 2008 1.0 8V"], result.Unmatched);
    }

    [Fact]
    public void Modelo_do_ML_pode_ser_o_comeco_do_nosso()
    {
        var table = new VehicleCandidate[]
        {
            new(GolG5Flex10, GolModel, "Volkswagen", "Gol G5", "1.0 8V Flex", 2009, 2012),
            new(GolG6, Guid.CreateVersion7(), "Volkswagen", "Gol G6", "1.0 8V Flex", 2013, 2016),
        };
        var result = ChannelCompatibilityRules.Match([new("Volkswagen", "Gol", 2011, "1.0 8V"), new("Volkswagen", "Gol", 2014, "1.0 8V")], table);
        Assert.Equal([new CompatibilityMatch(GolG5Flex10, 2011, 2011), new CompatibilityMatch(GolG6, 2014, 2014)], result.Matches);
    }

    [Fact]
    public void Marca_ou_modelo_fora_da_tabela_fica_de_fora_sem_repetir()
    {
        var result = ChannelCompatibilityRules.Match([new("Fiat", "Uno", 2012, "1.0 Fire"), new("Fiat", "Uno", 2012, "1.0 Fire"), new("Ford", "Fiesta", 2015, null)], Table);
        Assert.Empty(result.Matches);
        Assert.Equal(["Fiat Uno 2012 1.0 Fire", "Ford Fiesta 2015"], result.Unmatched);
    }
}
