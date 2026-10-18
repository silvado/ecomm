using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ecommerce.Application.Ai;
using Ecommerce.Application.Reports;
using Ecommerce.Domain.Ai;

namespace Ecommerce.Infrastructure.Reports;

/// <summary>
/// Abertura do resumo escrita pela IA (RF34 CA2): recebe os números prontos e só redige. O texto passa por
/// <see cref="DailySummaryRules.AcceptAiText"/>; recusado ou com a IA fora do ar, o e-mail sai só com os números.
/// </summary>
public static class DailySummaryWriter
{
    internal const string Instructions = """
        Você escreve a abertura do resumo diário que uma loja de autopeças do Brasil recebe por e-mail.
        Recebe os números de ontem já calculados. Escreva de 2 a 4 frases curtas, em português, tom cordial e direto,
        destacando o que pede ação (perguntas sem resposta, peças paradas) e as vendas.
        Use só os números que vieram, exatamente como vieram. Nunca faça contas, porcentagens, comparações nem previsões.
        Não invente fatos, não dê conselhos de preço e não cite clientes.
        """;

    internal static readonly JsonObject OutputSchema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["texto"] = new JsonObject { ["type"] = "string" } },
        ["required"] = new JsonArray("texto"),
        ["additionalProperties"] = false,
    };

    /// <summary>Resposta da IA simulada (dev e testes): sem números, passa na conferência.</summary>
    internal static readonly JsonObject DevSample = new()
    {
        ["texto"] = "Bom dia! Aqui está o resumo de ontem da sua loja. Vale olhar primeiro as perguntas que esperam resposta.",
    };

    public static AiRequest Request(DailySummaryFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var prompt = new StringBuilder()
            .Append("Dia: ").AppendLine(facts.Day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture))
            .Append("Vendas: ").Append(facts.Orders.ToString(CultureInfo.InvariantCulture)).Append(" pedido(s), total ").AppendLine(DailySummaryEmail.Money(facts.Total));
        foreach (var s in facts.Sales)
            prompt.Append("- ").Append(DailySummaryRules.OriginLabel(s.Origin)).Append(": ").Append(s.Orders.ToString(CultureInfo.InvariantCulture))
                .Append(" pedido(s), ").AppendLine(DailySummaryEmail.Money(s.Total));
        prompt.Append("Peças sem venda há mais de ").Append(DailySummaryFacts.StaleDays.ToString(CultureInfo.InvariantCulture)).Append(" dias: ")
            .AppendLine(facts.StaleParts.ToString(CultureInfo.InvariantCulture))
            .Append("Perguntas de compradores sem resposta: ").AppendLine(facts.PendingQuestions.ToString(CultureInfo.InvariantCulture));
        var summary = $"resumo de {facts.Day:yyyy-MM-dd}: {facts.Orders} pedido(s), {facts.StaleParts} peça(s) parada(s), {facts.PendingQuestions} pergunta(s) pendente(s)";
        return new AiRequest(AiFeature.DailySummary, Instructions, prompt.ToString(), [], OutputSchema, summary, MaxOutputTokens: 512);
    }

    public static string? Read(JsonElement output) =>
        output.ValueKind == JsonValueKind.Object && output.TryGetProperty("texto", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
}
