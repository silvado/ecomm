using Ecommerce.Domain.Orders;

namespace Ecommerce.Domain.Tests.Orders;

/// <summary>Etapas do pedido depois do checkout (RF17): análise, envio, entrega, retirada e cancelamento pela loja.</summary>
public sealed class OrderLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static CustomerOrder Order(bool pickup = false)
    {
        var line = new OrderLine(Guid.CreateVersion7(), "K-1", "Farol", 100m, 1, Guid.CreateVersion7());
        var buyer = new BuyerDetails("Maria da Silva", "maria@exemplo.com.br", "48999991234", "12345678909");
        return pickup
            ? CustomerOrder.Place(Guid.CreateVersion7(), 7, new byte[32], buyer, null, null, "Rua da Loja, 1", [line], Now, Now.AddMinutes(30))
            : CustomerOrder.Place(Guid.CreateVersion7(), 7, new byte[32], buyer,
                new DeliveryAddress("88015200", "Rua A", "1", null, "Centro", "Florianópolis", "SC"),
                new ShippingChoice("fake-pac", "Correios", "PAC", 20m, 5), null, [line], Now, Now.AddMinutes(30));
    }

    private static CustomerOrder Paid(bool pickup = false)
    {
        var order = Order(pickup);
        order.MarkPaid(Now);
        return order;
    }

    [Fact]
    public void Entrega_passa_por_enviado_com_rastreio_e_entregue()
    {
        var order = Paid();

        order.MarkShipped("  BR123456789BR ", Now.AddHours(1));
        Assert.Equal((OrderStatus.Shipped, "BR123456789BR", Now.AddHours(1)), (order.Status, order.TrackingCode, order.ShippedAt));

        order.MarkDelivered(Now.AddDays(3));
        Assert.Equal((OrderStatus.Delivered, Now.AddDays(3)), (order.Status, order.DeliveredAt));
        Assert.False(order.CanBeCanceledByStore);
    }

    [Fact]
    public void Rastreio_e_opcional_e_tem_limite()
    {
        var order = Paid();
        Assert.Throws<ArgumentException>(() => order.MarkShipped(new string('x', CustomerOrder.TrackingCodeMaxLength + 1), Now));
        Assert.Equal(OrderStatus.Paid, order.Status);

        order.MarkShipped("   ", Now);
        Assert.Null(order.TrackingCode);
    }

    [Fact]
    public void Retirada_vai_direto_de_pago_para_retirado()
    {
        var order = Paid(pickup: true);

        Assert.Throws<InvalidOperationException>(() => order.MarkShipped(null, Now));
        order.MarkPickedUp(Now.AddHours(2));

        Assert.Equal((OrderStatus.PickedUp, Now.AddHours(2)), (order.Status, order.DeliveredAt));
    }

    [Fact]
    public void Etapa_fora_de_ordem_e_recusada()
    {
        Assert.Throws<InvalidOperationException>(() => Order().MarkShipped(null, Now)); // não pago
        Assert.Throws<InvalidOperationException>(() => Paid().MarkDelivered(Now)); // não enviado
        Assert.Throws<InvalidOperationException>(() => Paid().MarkPickedUp(Now)); // é entrega
        Assert.Throws<InvalidOperationException>(() => Paid().ApproveReview(Now)); // não está em análise
    }

    [Fact]
    public void Analise_aprovada_volta_a_pago_e_guarda_a_primeira_data_de_pagamento()
    {
        var never = Order();
        never.FlagForReview("Valor diferente");
        never.ApproveReview(Now.AddMinutes(5));
        Assert.Equal((OrderStatus.Paid, Now.AddMinutes(5), (string?)null), (never.Status, never.PaidAt, never.ReviewReason));

        var refunded = Paid();
        refunded.FlagForReview("Estornado");
        refunded.ApproveReview(Now.AddDays(1));
        Assert.Equal(Now, refunded.PaidAt);
    }

    [Theory]
    [InlineData(OrderStatus.PendingPayment)]
    [InlineData(OrderStatus.NeedsReview)]
    [InlineData(OrderStatus.Paid)]
    [InlineData(OrderStatus.Shipped)]
    public void Loja_cancela_antes_da_entrega_com_motivo(OrderStatus status)
    {
        var order = Order();
        if (status != OrderStatus.PendingPayment) order.MarkPaid(Now);
        if (status == OrderStatus.NeedsReview) order.FlagForReview("x");
        if (status == OrderStatus.Shipped) order.MarkShipped(null, Now);

        order.CancelByStore("  Comprador   desistiu ", Now.AddHours(1));

        Assert.Equal((OrderStatus.Canceled, CancellationSource.Store, "Comprador desistiu", Now.AddHours(1)),
            (order.Status, order.CanceledBy, order.CancelReason, order.CanceledAt));
    }

    [Fact]
    public void Cancelamento_exige_motivo_e_nao_vale_depois_da_entrega()
    {
        var order = Paid();
        Assert.Throws<ArgumentException>(() => order.CancelByStore("   ", Now));
        Assert.Throws<ArgumentException>(() => order.CancelByStore(new string('x', CustomerOrder.CancelReasonMaxLength + 1), Now));
        Assert.Equal(OrderStatus.Paid, order.Status);

        var picked = Paid(pickup: true);
        picked.MarkPickedUp(Now);
        Assert.Throws<InvalidOperationException>(() => picked.CancelByStore("Devolução", Now));

        var canceled = Paid();
        canceled.CancelByStore("Defeito", Now);
        Assert.Throws<InvalidOperationException>(() => canceled.CancelByStore("De novo", Now));
    }

    [Fact]
    public void Cancelado_pela_loja_nao_aceita_mais_pagamento()
    {
        var order = Order();
        order.CancelByStore("Peça danificada", Now);

        Assert.False(order.AcceptsPayment);
        Assert.Throws<InvalidOperationException>(() => order.MarkPaid(Now));
    }
}
