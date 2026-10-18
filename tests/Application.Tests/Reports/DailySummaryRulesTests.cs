using Ecommerce.Application.Reports;
using Ecommerce.Domain.Orders;

namespace Ecommerce.Application.Tests.Reports;

/// <summary>RF34 CA2: o texto da IA só entra com os números das consultas; CA1: horário da loja.</summary>
public sealed class DailySummaryRulesTests
{
    private static readonly DailySummaryFacts Facts = new(
        new DateOnly(2026, 10, 17),
        [new ChannelSales(OrderOrigin.Counter, 2, 240m), new ChannelSales(OrderOrigin.MercadoLivre, 1, 1350.5m)],
        StaleParts: 4,
        [new StalePart(Guid.NewGuid(), "FAR-1", "Farol", 1, 132)],
        PendingQuestions: 3,
        OldestPendingQuestion: null);

    [Theory]
    [InlineData("Bom dia! Ontem foram 3 pedidos, somando R$ 1.590,50.")]
    [InlineData("Você tem 3 perguntas esperando e 4 peças paradas há mais de 90 dias.")]
    [InlineData("No balcão saíram 2 pedidos (R$ 240,00); no Mercado Livre, 1 pedido de R$ 1.350,50.")]
    [InlineData("Resumo de 17/10/2026: uma peça está parada há 132 dias.")]
    [InlineData("Bom dia! Comece pelas perguntas sem resposta.")]
    public void Texto_com_os_numeros_dos_fatos_e_aceito(string text) => Assert.Equal(text, DailySummaryRules.AcceptAiText(text, Facts));

    [Theory]
    [InlineData("Ontem foram 5 pedidos.")] // número inventado
    [InlineData("As vendas cresceram 20% em relação à semana passada.")] // conta e comparação
    [InlineData("O ticket médio foi R$ 530,17.")] // conta feita pela IA
    [InlineData("")]
    [InlineData("   ")]
    public void Texto_com_numero_fora_dos_fatos_e_descartado(string text) => Assert.Null(DailySummaryRules.AcceptAiText(text, Facts));

    [Fact]
    public void Texto_longo_demais_e_descartado() =>
        Assert.Null(DailySummaryRules.AcceptAiText(new string('a', DailySummaryRules.MaxAiTextLength + 1), Facts));

    [Theory]
    [InlineData(6, 7, false)]
    [InlineData(7, 7, true)]
    [InlineData(15, 7, true)] // Worker parado às 7h: sai assim que voltar, no mesmo dia
    [InlineData(0, 0, true)]
    public void Horario(int hourNow, int configured, bool due) =>
        Assert.Equal(due, DailySummaryRules.IsDue(new DateTime(2026, 10, 18, hourNow, 5, 0, DateTimeKind.Unspecified), configured));
}
