using Ecommerce.Domain.Orders;

namespace Ecommerce.Domain.Tests.Orders;

/// <summary>Devolução (RF20, Q35): parcial ou total, nunca acima do vendido; "Devolvido" só quando tudo volta.</summary>
public sealed class OrderReturnTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Farol = Guid.CreateVersion7();
    private static readonly Guid Lanterna = Guid.CreateVersion7();
    private static readonly Guid User = Guid.CreateVersion7();

    private static CustomerOrder Delivered()
    {
        var buyer = new BuyerDetails("Maria da Silva", "maria@exemplo.com.br", "48999991234", "12345678909");
        var order = CustomerOrder.Place(Guid.CreateVersion7(), 7, new byte[32], buyer, null, null, "Balcão", [
            new OrderLine(Farol, "K-1", "Farol", 100m, 2, Guid.CreateVersion7()),
            new OrderLine(Lanterna, "K-2", "Lanterna", 50m, 1, Guid.CreateVersion7()),
        ], Now, Now.AddMinutes(30));
        order.MarkPaid(Now);
        order.MarkPickedUp(Now.AddHours(1));
        return order;
    }

    private static OrderReturn Return(CustomerOrder order, List<OrderReturn> previous, params ReturnRequestLine[] lines)
    {
        var r = order.RegisterReturn(lines, previous, ReturnReason.Defect, " lente  trincada ", 100m, RefundMethod.Pix, User, Now.AddDays(3));
        previous.Add(r);
        return r;
    }

    [Fact]
    public void Devolucao_parcial_mantem_a_situacao_e_registra_avaria()
    {
        var order = Delivered();
        var returns = new List<OrderReturn>();

        var r = Return(order, returns, new ReturnRequestLine(Farol, 1, Restock: false));

        Assert.Equal(OrderStatus.PickedUp, order.Status);
        Assert.True(order.HasReturns);
        Assert.Equal(("K-1", 1, false), (r.Lines[0].InternalCode, r.Lines[0].Quantity, r.Lines[0].Restock));
        Assert.Equal(("lente trincada", 100m, RefundMethod.Pix), (r.Note, r.RefundAmount, r.RefundMethod));
        Assert.Equal(1, CustomerOrder.ReturnedQuantity(returns, Farol));
    }

    [Fact]
    public void Quando_tudo_volta_o_pedido_fica_devolvido_e_nao_aceita_mais()
    {
        var order = Delivered();
        var returns = new List<OrderReturn>();
        Return(order, returns, new ReturnRequestLine(Farol, 1, true));
        Return(order, returns, new ReturnRequestLine(Farol, 1, true), new ReturnRequestLine(Lanterna, 1, true));

        Assert.Equal((OrderStatus.Returned, Now.AddDays(3)), (order.Status, order.ReturnedAt));
        Assert.Throws<InvalidOperationException>(() => Return(order, returns, new ReturnRequestLine(Farol, 1, true)));
    }

    [Fact]
    public void Nao_devolve_acima_do_vendido_nem_peca_fora_do_pedido()
    {
        var order = Delivered();
        var returns = new List<OrderReturn>();
        Return(order, returns, new ReturnRequestLine(Farol, 2, true));

        var over = Assert.Throws<ArgumentException>(() => Return(order, returns, new ReturnRequestLine(Farol, 1, true)));
        Assert.Contains("só 0 unidade(s)", over.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => Return(order, returns, new ReturnRequestLine(Guid.CreateVersion7(), 1, true)));
        Assert.Throws<ArgumentException>(() => Return(order, returns, new ReturnRequestLine(Lanterna, -1, true)));
        Assert.Throws<ArgumentException>(() => Return(order, returns, new ReturnRequestLine(Lanterna, 0, true)));
        Assert.Throws<ArgumentException>(() => Return(order, returns, new ReturnRequestLine(Lanterna, 1, true), new ReturnRequestLine(Lanterna, 1, true)));
    }

    [Fact]
    public void Reembolso_entre_zero_e_o_total()
    {
        var order = Delivered();
        Assert.Throws<ArgumentException>(() => order.RegisterReturn([new(Lanterna, 1, true)], [], ReturnReason.Regret, null, 250.01m, RefundMethod.Cash, User, Now));
        Assert.Throws<ArgumentException>(() => order.RegisterReturn([new(Lanterna, 1, true)], [], ReturnReason.Regret, null, -1m, RefundMethod.Cash, User, Now));

        var noRefund = order.RegisterReturn([new(Lanterna, 1, true)], [], ReturnReason.Regret, null, null, RefundMethod.Cash, User, Now);
        Assert.Null(noRefund.RefundMethod);
    }

    [Fact]
    public void So_pedido_entregue_ou_retirado_aceita_devolucao()
    {
        var buyer = new BuyerDetails("Maria da Silva", "maria@exemplo.com.br", "48999991234", "12345678909");
        var order = CustomerOrder.Place(Guid.CreateVersion7(), 8, new byte[32], buyer, null, null, "Balcão",
            [new OrderLine(Farol, "K-1", "Farol", 100m, 1, Guid.CreateVersion7())], Now, Now.AddMinutes(30));
        order.MarkPaid(Now);

        Assert.Throws<InvalidOperationException>(() => order.RegisterReturn([new(Farol, 1, true)], [], ReturnReason.Regret, null, null, null, User, Now));
    }

    [Fact]
    public void Venda_de_balcao_com_devolucao_nao_pode_mais_ser_cancelada()
    {
        var sale = CustomerOrder.PlaceCounterSale(Guid.CreateVersion7(), 9, new byte[32], null, CounterPaymentMethod.Pix,
            [new OrderLine(Farol, "K-1", "Farol", 100m, 2, Guid.CreateVersion7())], Now);
        Assert.True(sale.CanBeCanceledByStore);

        sale.RegisterReturn([new(Farol, 1, true)], [], ReturnReason.WrongPart, null, null, null, User, Now);

        Assert.False(sale.CanBeCanceledByStore);
        Assert.Throws<InvalidOperationException>(() => sale.CancelByStore("lançada errado", Now));
    }
}
