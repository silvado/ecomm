using Ecommerce.Domain.Inventory;
using Ecommerce.Domain.Shipping;

namespace Ecommerce.Domain.Tests.Shipping;

public sealed class ShippingSettingsTests
{
    [Theory]
    [InlineData("01310-100", "01310100", "01310-100")]
    [InlineData(" 01.310-100 ", "01310100", "01310-100")]
    [InlineData("88015200", "88015200", "88015-200")]
    public void Cep_e_normalizado(string input, string value, string formatted)
    {
        var cep = PostalCode.Parse(input);

        Assert.Equal((value, formatted), (cep.Value, cep.Formatted));
    }

    [Theory]
    [InlineData("0131010")]
    [InlineData("013101000")]
    [InlineData("01310-10A")]
    [InlineData("00000-000")]
    [InlineData("")]
    public void Cep_invalido(string input) => Assert.False(PostalCode.TryParse(input, out _));

    [Fact]
    public void Loja_configura_origem_e_retirada()
    {
        var settings = new StoreSettings(Guid.NewGuid());

        settings.ConfigureShipping("01310-100", pickupEnabled: true, "  Rua das Peças, 100 — seg a sex, 8h às 18h  ");

        Assert.Equal(("01310100", true, "Rua das Peças, 100 — seg a sex, 8h às 18h"), (settings.OriginPostalCode, settings.PickupEnabled, settings.PickupAddress));
    }

    [Fact]
    public void Retirada_exige_endereco_e_cep_invalido_nao_grava()
    {
        var settings = new StoreSettings(Guid.NewGuid());
        settings.ConfigureShipping("01310100", false, null);

        Assert.Throws<ArgumentException>(() => settings.ConfigureShipping("01310100", pickupEnabled: true, "   "));
        Assert.Throws<ArgumentException>(() => settings.ConfigureShipping("123", false, null));
        Assert.Equal("01310100", settings.OriginPostalCode);
    }

    [Fact]
    public void Sem_cep_a_loja_nao_entrega()
    {
        var settings = new StoreSettings(Guid.NewGuid());

        settings.ConfigureShipping(" ", pickupEnabled: false, null);

        Assert.Null(settings.OriginPostalCode);
    }
}
