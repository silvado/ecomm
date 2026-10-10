using System.Security.Cryptography;
using Ecommerce.Domain.Orders;

namespace Ecommerce.Domain.Tests.Orders;

public sealed class OrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.CreateVersion7();

    /// <summary>CPF válido montado a partir de 9 dígitos sorteados (nenhum CPF real no repositório).</summary>
    private static string ValidCpf(string? body = null)
    {
        body ??= string.Concat(Enumerable.Range(0, 9).Select(_ => RandomNumberGenerator.GetInt32(1, 10)));
        static int Digit(string b)
        {
            var sum = 0;
            for (var i = 0; i < b.Length; i++) sum += (b[i] - '0') * (b.Length + 1 - i);
            return sum % 11 < 2 ? 0 : 11 - sum % 11;
        }
        var first = Digit(body);
        return body + first + Digit(body + first);
    }

    private static BuyerDetails Buyer(string? cpf = null) => new("  Maria   da Silva ", " Maria@Exemplo.com.BR ", "(48) 99999-1234", cpf ?? ValidCpf());

    private static DeliveryAddress Address() => new("88015-200", "Rua Felipe Schmidt", "100", "", "Centro", "Florianópolis", "sc");

    private static OrderLine Line(decimal price = 100m, int quantity = 1) =>
        new(Guid.CreateVersion7(), "K-1", "Farol", price, quantity, Guid.CreateVersion7());

    private static CustomerOrder Place(IReadOnlyList<OrderLine>? lines = null, ShippingChoice? shipping = null, string? pickup = null, DeliveryAddress? address = null) =>
        CustomerOrder.Place(Tenant, 7, new byte[32], Buyer(), address, shipping, pickup, lines ?? [Line()], Now, Now.AddMinutes(30));

    [Fact]
    public void Cpf_valida_digitos_e_aceita_pontuacao()
    {
        var cpf = ValidCpf("123456789");
        Assert.Equal("12345678909", cpf);
        Assert.Equal(cpf, Cpf.Parse("123.456.789-09").Value);

        Assert.False(Cpf.TryParse("123.456.789-08", out _));
        Assert.False(Cpf.TryParse("123.456.789-90", out _));
        Assert.False(Cpf.TryParse("111.111.111-11", out _));
        Assert.False(Cpf.TryParse("1234567890", out _));
        Assert.False(Cpf.TryParse("1234567890a", out _));
    }

    [Fact]
    public void Pedido_com_entrega_normaliza_dados_e_soma_frete()
    {
        var order = Place([Line(120m, 2), Line(35.5m)], new ShippingChoice("fake-economico", "Fake", "Econômico", 25.9m, 7), address: Address());

        Assert.Equal((OrderStatus.PendingPayment, OrderOrigin.Site, DeliveryMethod.Shipping), (order.Status, order.Origin, order.DeliveryMethod));
        Assert.Equal(("Maria da Silva", "maria@exemplo.com.br", "48999991234"), (order.BuyerName, order.BuyerEmail, order.BuyerPhone));
        Assert.Equal(new DeliveryAddress("88015200", "Rua Felipe Schmidt", "100", null, "Centro", "Florianópolis", "SC"), order.Address);
        Assert.Equal((275.5m, 25.9m, 301.4m), (order.ItemsTotal, order.ShippingTotal, order.Total));
        Assert.All(order.Items, i => Assert.Equal((Tenant, order.Id), (i.TenantId, i.OrderId)));
        Assert.Equal(Now.AddMinutes(30), order.PaymentDeadline);
    }

    [Fact]
    public void Pedido_com_retirada_nao_tem_frete_nem_endereco()
    {
        var order = Place(pickup: "Rua das Peças, 100");

        Assert.Equal((DeliveryMethod.Pickup, 0m, 100m, "Rua das Peças, 100"), (order.DeliveryMethod, order.ShippingTotal, order.Total, order.PickupAddress));
        Assert.Null(order.Address);
    }

    [Theory]
    [InlineData("Ma", "a@b.com", "4899999123", "Informe o nome completo.")]
    [InlineData("Maria", "a@b", "4899999123", "Informe um e-mail válido.")]
    [InlineData("Maria", "maria", "4899999123", "Informe um e-mail válido.")]
    [InlineData("Maria", "a@b.com", "999991234", "Informe o telefone com DDD.")]
    [InlineData("Maria", "a@b.com", "0489999123", "Informe o telefone com DDD.")]
    public void Dados_do_comprador_invalidos_sao_recusados(string name, string email, string phone, string message)
    {
        var e = Assert.Throws<ArgumentException>(() => CustomerOrder.NormalizeBuyer(new BuyerDetails(name, email, phone, ValidCpf())));
        Assert.StartsWith(message, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cpf_invalido_e_recusado()
    {
        var e = Assert.Throws<ArgumentException>(() => CustomerOrder.NormalizeBuyer(Buyer("12345678900")));
        Assert.StartsWith("CPF inválido.", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("8801520", "Rua", "1", "Centro", "Floripa", "SC", "CEP inválido")]
    [InlineData("88015200", " ", "1", "Centro", "Floripa", "SC", "Informe a rua.")]
    [InlineData("88015200", "Rua", "", "Centro", "Floripa", "SC", "Informe o número")]
    [InlineData("88015200", "Rua", "1", "", "Floripa", "SC", "Informe o bairro.")]
    [InlineData("88015200", "Rua", "1", "Centro", "", "SC", "Informe a cidade.")]
    [InlineData("88015200", "Rua", "1", "Centro", "Floripa", "XX", "Informe a UF.")]
    public void Endereco_invalido_e_recusado(string cep, string street, string number, string district, string city, string state, string message)
    {
        var e = Assert.Throws<ArgumentException>(() => CustomerOrder.NormalizeAddress(new DeliveryAddress(cep, street, number, null, district, city, state)));
        Assert.StartsWith(message, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Entrega_exige_endereco_e_retirada_exige_endereco_da_loja()
    {
        var shipping = new ShippingChoice("x", "Fake", "Econômico", 10m, 3);
        Assert.StartsWith("Informe o endereço de entrega.", Assert.Throws<ArgumentException>(() => Place(shipping: shipping)).Message, StringComparison.Ordinal);
        Assert.StartsWith("Esta loja não oferece retirada.", Assert.Throws<ArgumentException>(() => Place(pickup: " ")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Itens_precisam_ser_validos()
    {
        var repeated = Line();
        Assert.Throws<ArgumentException>(() => Place([], pickup: "Balcão"));
        Assert.Throws<ArgumentException>(() => Place([repeated, repeated], pickup: "Balcão"));
        Assert.Throws<ArgumentException>(() => Place([Line(quantity: 0)], pickup: "Balcão"));
        Assert.Throws<ArgumentException>(() => Place([Line(price: 0m)], pickup: "Balcão"));
        Assert.Throws<ArgumentException>(() => Place(Enumerable.Range(0, CustomerOrder.MaxLines + 1).Select(_ => Line()).ToList(), pickup: "Balcão"));
    }
}
