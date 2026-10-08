using Ecommerce.Domain.Catalog;

namespace Ecommerce.Domain.Tests.Catalog;

public sealed class PartTests
{
    [Fact]
    public void Peca_exige_tenant()
    {
        Assert.Throws<ArgumentException>(() => new Part(Guid.Empty, "A-1", "Farol", 10m));
    }

    [Fact]
    public void Preco_nao_pode_ser_negativo()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Part(Guid.CreateVersion7(), "A-1", "Farol", -1m));
    }

    [Fact]
    public void Peca_valida_recebe_id_e_tenant()
    {
        var tenant = Guid.CreateVersion7();

        var part = new Part(tenant, "A-1", "Farol", 10m);

        Assert.NotEqual(Guid.Empty, part.Id);
        Assert.Equal(tenant, part.TenantId);
    }
}
