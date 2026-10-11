using Ecommerce.Application.Payments;

namespace Ecommerce.Application.Tests.Payments;

public sealed class OrderReferenceTests
{
    [Theory]
    [InlineData(1L)]
    [InlineData(42L)]
    [InlineData(99_999_999_999_999_999L)]
    public void Numero_vai_e_volta_com_o_prefixo(long number)
    {
        var reference = OrderReference.FromNumber(number);

        Assert.StartsWith("PED", reference, StringComparison.Ordinal);
        Assert.True(reference.Length <= 20);
        Assert.Equal(number, OrderReference.NumberOf(reference));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("PED")]
    [InlineData("42")]
    [InlineData("ABC42")]
    [InlineData("ped42")]
    [InlineData("PED-42")]
    [InlineData("PED 42")]
    [InlineData("PED42x")]
    [InlineData("PED999999999999999999")]
    [InlineData("01a12944-e5ec-7501-af3a-6040e349f58b")]
    public void Referencia_que_nao_e_de_numero_de_pedido_e_nula(string? reference)
    {
        Assert.Null(OrderReference.NumberOf(reference));
    }
}
