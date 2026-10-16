using Ecommerce.Domain.Billing;
using Ecommerce.Domain.Platform;

namespace Ecommerce.Domain.Tests.Billing;

/// <summary>RF38 — assinatura: vencimentos a partir da publicação e ciclo de vida da fatura.</summary>
public sealed class SubscriptionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Primeira_mensalidade_vence_na_publicacao_e_as_seguintes_no_mesmo_dia()
    {
        var subscription = Subscription.Start(Guid.NewGuid(), new DateOnly(2026, 10, 16), Now);

        Assert.Equal(
            [(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 16)), (new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 16)),
             (new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 16))],
            subscription.PeriodsUntil(new DateOnly(2026, 12, 20)).ToList());
    }

    [Fact]
    public void Publicada_depois_do_dia_28_vence_no_dia_28_dos_meses_seguintes()
    {
        var subscription = Subscription.Start(Guid.NewGuid(), new DateOnly(2027, 1, 31), Now);

        Assert.Equal(28, subscription.DueDay);
        Assert.Equal([new DateOnly(2027, 1, 31), new DateOnly(2027, 2, 28), new DateOnly(2027, 3, 28)],
            subscription.PeriodsUntil(new DateOnly(2027, 3, 31)).Select(p => p.DueDate).ToList());
    }

    [Fact]
    public void Nada_antes_da_publicacao()
    {
        var subscription = Subscription.Start(Guid.NewGuid(), new DateOnly(2026, 10, 16), Now);

        Assert.Empty(subscription.PeriodsUntil(new DateOnly(2026, 10, 15)));
    }

    [Fact]
    public void Mensalidade_descreve_competencia_e_plano()
    {
        var invoice = SubscriptionInvoice.Monthly(Guid.NewGuid(), new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 16), "Profissional", 299.90m, Now);

        Assert.Equal("Mensalidade 11/2026 — plano Profissional", invoice.Description);
        Assert.Equal((InvoiceKind.Monthly, InvoiceStatus.Open), (invoice.Kind, invoice.Status));
        Assert.Throws<ArgumentException>(() => SubscriptionInvoice.Monthly(Guid.NewGuid(), new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 16), "X", 10m, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10.001)]
    public void Valor_precisa_ser_positivo_em_centavos(decimal amount) =>
        Assert.Throws<ArgumentException>(() => SubscriptionInvoice.Setup(Guid.NewGuid(), amount, new DateOnly(2026, 11, 1), Now));

    [Fact]
    public void Vencida_so_depois_do_dia_do_vencimento_e_enquanto_aberta()
    {
        var invoice = SubscriptionInvoice.Setup(Guid.NewGuid(), 1500m, new DateOnly(2026, 11, 1), Now);

        Assert.False(invoice.IsOverdue(new DateOnly(2026, 11, 1)));
        Assert.True(invoice.IsOverdue(new DateOnly(2026, 11, 2)));
        invoice.MarkPaid(1500m, InvoicePaidVia.Manual, "Pix", Now);
        Assert.False(invoice.IsOverdue(new DateOnly(2026, 11, 2)));
    }

    [Fact]
    public void Pagamento_repetido_nao_muda_nada()
    {
        var invoice = SubscriptionInvoice.Setup(Guid.NewGuid(), 1500m, new DateOnly(2026, 11, 1), Now);

        Assert.True(invoice.MarkPaid(1500m, InvoicePaidVia.Gateway, null, Now));
        Assert.False(invoice.MarkPaid(1500m, InvoicePaidVia.Manual, "de novo", Now.AddDays(1)));
        Assert.Equal((InvoicePaidVia.Gateway, Now, (string?)null), (invoice.PaidVia!.Value, invoice.PaidAt!.Value, invoice.Note));
    }

    [Fact]
    public void So_fatura_aberta_recebe_cobranca_ou_e_cancelada()
    {
        var invoice = SubscriptionInvoice.Setup(Guid.NewGuid(), 1500m, new DateOnly(2026, 11, 1), Now);
        invoice.Cancel("Cortesia");

        Assert.Equal((InvoiceStatus.Canceled, "Cortesia"), (invoice.Status, invoice.Note));
        Assert.Throws<InvalidOperationException>(() => invoice.Cancel(null));
        Assert.Throws<InvalidOperationException>(() => invoice.AttachCharge("x", "https://gateway.test/x"));
    }

    [Fact]
    public void Preco_do_plano_aceita_zero_e_recusa_fracao_de_centavo()
    {
        var plan = new Plan(Guid.NewGuid(), "teste", "Teste");

        plan.SetMonthlyPrice(0m);
        plan.SetMonthlyPrice(149.90m);
        Assert.Equal(149.90m, plan.MonthlyPrice);
        Assert.Throws<ArgumentException>(() => plan.SetMonthlyPrice(-1m));
        Assert.Throws<ArgumentException>(() => plan.SetMonthlyPrice(1.001m));
    }
}
