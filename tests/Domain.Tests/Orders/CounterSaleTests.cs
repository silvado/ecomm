using Ecommerce.Domain.Inventory;
using Ecommerce.Domain.Orders;

namespace Ecommerce.Domain.Tests.Orders;

/// <summary>Venda de balcão (RF18): nasce paga e retirada, cliente opcional (CPF ou CNPJ) e pode ser cancelada.</summary>
public sealed class CounterSaleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static CustomerOrder Sale(CounterCustomer? customer = null, CounterPaymentMethod method = CounterPaymentMethod.Pix, params OrderLine[] lines) =>
        CustomerOrder.PlaceCounterSale(Guid.CreateVersion7(), 9, new byte[32], customer, method,
            lines.Length > 0 ? lines : [new OrderLine(Guid.CreateVersion7(), "K-1", "Farol", 100m, 2, Guid.CreateVersion7())], Now);

    [Fact]
    public void Nasce_paga_e_retirada_sem_prazo_nem_frete()
    {
        var sale = Sale(method: CounterPaymentMethod.Cash);

        Assert.Equal((OrderOrigin.Counter, OrderStatus.PickedUp, CounterPaymentMethod.Cash), (sale.Origin, sale.Status, sale.PaymentMethod));
        Assert.Equal((Now, Now, Now), (sale.PaidAt, sale.DeliveredAt, sale.PaymentDeadline));
        Assert.Equal((200m, 0m, 200m), (sale.ItemsTotal, sale.ShippingTotal, sale.Total));
        Assert.Equal((DeliveryMethod.Pickup, CustomerOrder.CounterPickup), (sale.DeliveryMethod, sale.PickupAddress));
        Assert.False(sale.AcceptsPayment);
    }

    [Fact]
    public void Sem_cliente_vira_consumidor_final()
    {
        var sale = Sale(new CounterCustomer("  ", null, ""));

        Assert.Equal((CustomerOrder.WalkInCustomer, "", "", "", null), (sale.BuyerName, sale.BuyerEmail, sale.BuyerPhone, sale.BuyerCpf, sale.BuyerCnpj));
    }

    [Fact]
    public void Documento_pode_ser_cpf_ou_cnpj_e_telefone_com_ddd()
    {
        var person = Sale(new CounterCustomer(" João  Souza ", "123.456.789-09", "(48) 99999-1234"));
        Assert.Equal(("João Souza", "12345678909", null, "48999991234"), (person.BuyerName, person.BuyerCpf, person.BuyerCnpj, person.BuyerPhone));

        var shop = Sale(new CounterCustomer("Oficina do Zé", "11.222.333/0001-81", null));
        Assert.Equal(("", "11222333000181"), (shop.BuyerCpf, shop.BuyerCnpj));

        var alphanumeric = Sale(new CounterCustomer(null, "12.ABC.345/01DE-35", null));
        Assert.Equal("12ABC34501DE35", alphanumeric.BuyerCnpj);
    }

    [Theory]
    [InlineData("Jo", null, null)]
    [InlineData(null, "123.456.789-00", null)]
    [InlineData(null, "11.222.333/0001-80", null)]
    [InlineData(null, "abc", null)]
    [InlineData(null, null, "9999-1234")]
    [InlineData(null, null, "(08) 99999-1234")]
    public void Cliente_informado_errado_e_recusado(string? name, string? document, string? phone) =>
        Assert.Throws<ArgumentException>(() => Sale(new CounterCustomer(name, document, phone)));

    [Fact]
    public void Linhas_e_forma_de_pagamento_sao_validadas()
    {
        var part = Guid.CreateVersion7();
        Assert.Throws<ArgumentException>(() => Sale(null, (CounterPaymentMethod)99));
        Assert.Throws<ArgumentException>(() => Sale(null, CounterPaymentMethod.Pix,
            new OrderLine(part, "K-1", "Farol", 100m, 1, Guid.CreateVersion7()), new OrderLine(part, "K-1", "Farol", 90m, 1, Guid.CreateVersion7())));
        Assert.Throws<ArgumentException>(() => Sale(null, CounterPaymentMethod.Pix, new OrderLine(part, "K-1", "Farol", 0m, 1, Guid.CreateVersion7())));
    }

    [Fact]
    public void Loja_pode_cancelar_venda_de_balcao_mas_nao_pedido_do_site_ja_retirado()
    {
        var sale = Sale();
        Assert.True(sale.CanBeCanceledByStore);
        sale.CancelByStore("Lançada em duplicidade", Now.AddMinutes(5));
        Assert.Equal((OrderStatus.Canceled, CancellationSource.Store), (sale.Status, sale.CanceledBy));
        Assert.False(sale.AcceptsPayment);

        var line = new OrderLine(Guid.CreateVersion7(), "K-1", "Farol", 100m, 1, Guid.CreateVersion7());
        var site = CustomerOrder.Place(Guid.CreateVersion7(), 8, new byte[32], new BuyerDetails("Maria da Silva", "maria@exemplo.com.br", "48999991234", "12345678909"),
            null, null, "Rua da Loja, 1", [line], Now, Now.AddMinutes(30));
        site.MarkPaid(Now);
        site.MarkPickedUp(Now);
        Assert.False(site.CanBeCanceledByStore);
    }

    [Fact]
    public void Reserva_da_baixa_direta_nasce_convertida()
    {
        var reservation = StockReservation.Sold(Guid.CreateVersion7(), Guid.CreateVersion7(), 2, "pedido 9", Now);

        Assert.Equal((ReservationStatus.Converted, 2, Now), (reservation.Status, reservation.Quantity, reservation.ExpiresAt));
        Assert.Throws<ArgumentOutOfRangeException>(() => StockReservation.Sold(Guid.CreateVersion7(), Guid.CreateVersion7(), 0, "pedido 9", Now));
    }
}
