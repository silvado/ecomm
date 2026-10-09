using Ecommerce.Domain.Catalog;
using Ecommerce.Domain.Vehicles;

namespace Ecommerce.Domain.Tests.Catalog;

public sealed class CompatibilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly VehicleVersionRange Gol = new(Guid.NewGuid(), 2009, 2012, Discontinued: false);

    private static Part NewPart() =>
        Part.Create(Guid.CreateVersion7(), "A-1", new PartDetails("Farol", null, PartCondition.Used, 100m, null, null, null, null, null), Now);

    [Fact]
    public void Sem_anos_vale_para_a_versao_inteira()
    {
        var compatibility = NewPart().AddCompatibility(Gol, null, null, CompatibilitySource.Manual, Now);

        Assert.Null(compatibility.YearFrom);
        Assert.Null(compatibility.YearTo);
    }

    [Fact]
    public void Anos_restringem_dentro_da_versao()
    {
        var compatibility = NewPart().AddCompatibility(Gol, 2011, 2012, CompatibilitySource.Manual, Now);

        Assert.Equal((2011, 2012), (compatibility.YearFrom, compatibility.YearTo));
    }

    [Fact]
    public void Faixa_igual_a_da_versao_vira_versao_inteira()
    {
        var compatibility = NewPart().AddCompatibility(Gol, 2009, 2012, CompatibilitySource.Manual, Now);

        Assert.Null(compatibility.YearFrom);
    }

    [Theory]
    [InlineData(2008, 2010)] // começa antes
    [InlineData(2011, 2013)] // termina depois
    [InlineData(2012, 2011)] // invertida
    [InlineData(2010, null)] // só um ano
    public void Anos_fora_da_versao_ou_incompletos_sao_recusados(int? from, int? to) =>
        Assert.Throws<ArgumentException>(() => NewPart().AddCompatibility(Gol, from, to, CompatibilitySource.Manual, Now));

    [Fact]
    public void Versao_ainda_em_producao_aceita_anos_recentes()
    {
        var current = new VehicleVersionRange(Guid.NewGuid(), 2020, null, false);

        Assert.Equal(2026, NewPart().AddCompatibility(current, 2024, 2026, CompatibilitySource.Manual, Now).YearTo);
    }

    [Fact]
    public void Mesma_compatibilidade_nao_se_repete_mas_outra_faixa_pode()
    {
        var part = NewPart();
        part.AddCompatibility(Gol, null, null, CompatibilitySource.Manual, Now);

        Assert.Throws<InvalidOperationException>(() => part.AddCompatibility(Gol, 2009, 2012, CompatibilitySource.Manual, Now)); // = inteira
        part.AddCompatibility(Gol, 2011, 2012, CompatibilitySource.Manual, Now);
        Assert.Equal(2, part.Compatibilities.Count);
    }

    [Fact]
    public void Versao_descontinuada_nao_recebe_compatibilidade_nova() =>
        Assert.Throws<ArgumentException>(() => NewPart().AddCompatibility(Gol with { Discontinued = true }, null, null, CompatibilitySource.Manual, Now));

    [Fact]
    public void Remover_compatibilidade()
    {
        var part = NewPart();
        var compatibility = part.AddCompatibility(Gol, null, null, CompatibilitySource.Manual, Now);

        part.RemoveCompatibility(compatibility.Id, Now);

        Assert.Empty(part.Compatibilities);
        Assert.Throws<KeyNotFoundException>(() => part.RemoveCompatibility(compatibility.Id, Now));
    }

    [Theory]
    [InlineData(1949, 2000)]
    [InlineData(2010, 2009)]
    [InlineData(2010, 2028)] // além do ano-modelo seguinte
    public void Anos_da_versao_do_veiculo_sao_validados(int from, int to) =>
        Assert.Throws<ArgumentException>(() => new VehicleVersion(Guid.NewGuid(), Guid.NewGuid(), "1.0 Flex", from, to, currentYear: 2026));

    [Fact]
    public void Versao_cobre_os_anos_da_faixa()
    {
        var version = new VehicleVersion(Guid.NewGuid(), Guid.NewGuid(), "1.0  8V   Flex", 2009, 2012, currentYear: 2026);

        Assert.Equal("1.0 8V Flex", version.Engine);
        Assert.True(version.Covers(2009) && version.Covers(2012));
        Assert.False(version.Covers(2008) || version.Covers(2013));
        Assert.True(new VehicleVersion(Guid.NewGuid(), Guid.NewGuid(), "1.0", 2020, null, 2026).Covers(2027));
    }
}
