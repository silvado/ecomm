using Ecommerce.Domain.Catalog;

namespace Ecommerce.Domain.Tests.Catalog;

public sealed class PartTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static PartDetails Details(string title = "Farol dianteiro esquerdo", decimal price = 350m,
        int? length = 60, int? width = 40, int? height = 30, int? weight = 2500, string[]? oem = null, string? description = null) =>
        new(title, description, PartCondition.Used, price, length, width, height, weight, oem);

    [Fact]
    public void Peca_nova_nasce_em_rascunho_com_codigo_normalizado()
    {
        var tenant = Guid.CreateVersion7();

        var part = Part.Create(tenant, "  far-001 ", Details(), Now);

        Assert.NotEqual(Guid.Empty, part.Id);
        Assert.Equal(tenant, part.TenantId);
        Assert.Equal("FAR-001", part.InternalCode);
        Assert.Equal(PartStatus.Draft, part.Status);
        Assert.Equal(Now, part.CreatedAt);
    }

    [Fact]
    public void Peca_exige_tenant() =>
        Assert.Throws<ArgumentException>(() => Part.Create(Guid.Empty, "A-1", Details(), Now));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10.555)]
    public void Preco_precisa_ser_positivo_com_duas_casas(decimal price) =>
        Assert.Throws<ArgumentException>(() => Part.Create(Guid.CreateVersion7(), "A-1", Details(price: price), Now));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Titulo_e_obrigatorio(string title) =>
        Assert.Throws<ArgumentException>(() => Part.Create(Guid.CreateVersion7(), "A-1", Details(title: title), Now));

    [Fact]
    public void Titulo_e_descricao_tem_limite()
    {
        Assert.Throws<ArgumentException>(() => Part.Create(Guid.CreateVersion7(), "A-1", Details(title: new string('x', Part.TitleMaxLength + 1)), Now));
        Assert.Throws<ArgumentException>(() => Part.Create(Guid.CreateVersion7(), "A-1", Details(description: new string('x', Part.DescriptionMaxLength + 1)), Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Codigo_interno_e_obrigatorio(string code) =>
        Assert.Throws<ArgumentException>(() => Part.Create(Guid.CreateVersion7(), code, Details(), Now));

    [Theory]
    [InlineData(0, 40, 30, 2500)]
    [InlineData(60, -1, 30, 2500)]
    [InlineData(60, 40, Part.MaxDimensionCm + 1, 2500)]
    [InlineData(60, 40, 30, Part.MaxWeightG + 1)]
    public void Medidas_informadas_precisam_ser_positivas_e_razoaveis(int length, int width, int height, int weight) =>
        Assert.Throws<ArgumentException>(() => Part.Create(Guid.CreateVersion7(), "A-1", Details(length: length, width: width, height: height, weight: weight), Now));

    [Fact]
    public void Sem_alguma_medida_nao_publica_em_canal_com_frete_calculado()
    {
        Assert.True(Part.Create(Guid.CreateVersion7(), "A-1", Details(), Now).HasShippingDimensions);
        Assert.False(Part.Create(Guid.CreateVersion7(), "A-1", Details(weight: null), Now).HasShippingDimensions);
        Assert.False(Part.Create(Guid.CreateVersion7(), "A-1", Details(length: null, width: null, height: null, weight: null), Now).HasShippingDimensions);
    }

    [Fact]
    public void Codigos_oem_sao_normalizados_e_sem_repeticao()
    {
        var part = Part.Create(Guid.CreateVersion7(), "A-1", Details(oem: ["1J0 615 301-M", "1j0615301m", " ", "8D0.941.029"]), Now);

        Assert.Equal(["1J0615301M", "8D0941029"], part.OemCodes.Select(c => c.Code).Order());
    }

    [Fact]
    public void Editar_os_oem_troca_a_lista()
    {
        var part = Part.Create(Guid.CreateVersion7(), "A-1", Details(oem: ["AAA1", "BBB2"]), Now);

        part.Update(Details(oem: ["BBB2", "CCC3"]), Now.AddMinutes(1));

        Assert.Equal(["BBB2", "CCC3"], part.OemCodes.Select(c => c.Code).Order());
        Assert.Equal(Now.AddMinutes(1), part.UpdatedAt);
    }

    [Fact]
    public void Limite_de_codigos_oem() =>
        Assert.Throws<ArgumentException>(() => Part.Create(Guid.CreateVersion7(), "A-1",
            Details(oem: Enumerable.Range(0, Part.MaxOemCodes + 1).Select(i => $"OEM{i}").ToArray()), Now));

    [Fact]
    public void Edicao_invalida_nao_altera_nada()
    {
        var part = Part.Create(Guid.CreateVersion7(), "A-1", Details(oem: ["AAA1"]), Now);

        Assert.Throws<ArgumentException>(() => part.Update(Details(title: "Outro", price: -5, oem: ["ZZZ9"]), Now.AddMinutes(1)));

        Assert.Equal("Farol dianteiro esquerdo", part.Title);
        Assert.Equal(["AAA1"], part.OemCodes.Select(c => c.Code));
        Assert.Equal(Now, part.UpdatedAt);
    }

    [Fact]
    public void Ativar_e_inativar()
    {
        var part = Part.Create(Guid.CreateVersion7(), "A-1", Details(), Now);

        part.Activate(Now);
        Assert.Equal(PartStatus.Active, part.Status);
        part.Deactivate(Now);
        Assert.Equal(PartStatus.Inactive, part.Status);
    }
}
