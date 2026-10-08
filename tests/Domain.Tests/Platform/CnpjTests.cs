using Ecommerce.Domain.Platform;

namespace Ecommerce.Domain.Tests.Platform;

public sealed class CnpjTests
{
    [Theory]
    [InlineData("11.222.333/0001-81", "11222333000181")]   // numérico clássico
    [InlineData("11222333000181", "11222333000181")]
    [InlineData("12.ABC.345/01DE-35", "12ABC34501DE35")]   // exemplo alfanumérico divulgado pela Receita (IN RFB 2.229/2024)
    [InlineData("12abc34501de35", "12ABC34501DE35")]       // minúsculas são normalizadas
    public void Aceita_CNPJ_valido_e_normaliza(string input, string expected)
    {
        Assert.True(Cnpj.TryParse(input, out var cnpj));
        Assert.Equal(expected, cnpj!.Value);
    }

    [Theory]
    [InlineData("11.222.333/0001-82")]   // DV errado
    [InlineData("12ABC34501DE36")]       // DV errado (alfanumérico)
    [InlineData("12ABC34501DEA5")]       // DV não numérico
    [InlineData("00000000000000")]       // repetido
    [InlineData("1122233300018")]        // 13 caracteres
    [InlineData("12ABC34501D@35")]       // caractere inválido
    [InlineData("")]
    [InlineData(null)]
    public void Rejeita_CNPJ_invalido(string? input)
    {
        Assert.False(Cnpj.TryParse(input, out _));
    }
}
