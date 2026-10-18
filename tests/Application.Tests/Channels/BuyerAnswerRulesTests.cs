using System.Text.Json;
using Ecommerce.Application.Channels;
using Ecommerce.Domain.Channels;

namespace Ecommerce.Application.Tests.Channels;

/// <summary>
/// RF31 CA2/CA3 — conjunto de avaliação (buyer-answer-eval.json): com a resposta automática ligada, só sai sozinha a
/// resposta simples sem compatibilidade, contato, preço ou desconto — mesmo quando a IA erra a classificação.
/// </summary>
public sealed class BuyerAnswerRulesTests
{
    private sealed record Case(string Name, string Question, AiBuyerAnswer Ai, bool HasVehicles, string[] Codes, string Expected, string? Note);

    private static readonly Lazy<IReadOnlyList<Case>> All = new(() =>
    {
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Channels", "buyer-answer-eval.json")));
        return file.RootElement.GetProperty("casos").EnumerateArray().Select(c =>
        {
            var ai = c.GetProperty("ia");
            return new Case(c.GetProperty("nome").GetString()!, c.GetProperty("pergunta").GetString()!,
                new AiBuyerAnswer(ai.GetProperty("tipo").GetString() == "simples", ai.GetProperty("compatibilidade").GetBoolean(),
                    ai.GetProperty("resposta").GetString()!, ai.GetProperty("motivo").GetString()!),
                c.GetProperty("temVeiculos").GetBoolean(), c.GetProperty("codigos").EnumerateArray().Select(x => x.GetString()!).ToArray(),
                c.GetProperty("esperado").GetString()!, c.TryGetProperty("nota", out var n) ? n.GetString() : null);
        }).ToList();
    });

    /// <summary>Um caso por nome (o nome aparece no resultado do teste).</summary>
    public static TheoryData<string> Cases() => new(All.Value.Select(c => c.Name));

    [Theory]
    [MemberData(nameof(Cases))]
    public void Conjunto_de_avaliacao(string name)
    {
        var c = All.Value.Single(x => x.Name == name);
        var decision = BuyerAnswerRules.Decide(c.Question, c.Ai, c.HasVehicles, autoAnswer: true, c.Codes);

        Assert.Equal(c.Expected == "automatica", decision.SendNow);
        Assert.Equal(c.Expected == "automatica" ? QuestionKind.Simple : QuestionKind.Complex, decision.Kind);
        if (c.Note is { } note) Assert.Contains(note, decision.Note ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Simples_com_a_resposta_automatica_desligada_fica_como_rascunho()
    {
        var decision = BuyerAnswerRules.Decide("Ainda tem?", new AiBuyerAnswer(true, false, "Temos sim.", ""), true, autoAnswer: false, []);

        Assert.Equal((QuestionKind.Simple, "Temos sim.", false), (decision.Kind, decision.Draft, decision.SendNow));
        Assert.Null(decision.Note);
    }

    [Fact]
    public void Resposta_longa_demais_e_cortada_e_vai_para_revisao()
    {
        var decision = BuyerAnswerRules.Decide("Como está?", new AiBuyerAnswer(true, false, new string('a', BuyerQuestion.AnswerMaxLength + 50), ""), true, true, []);

        Assert.Equal(BuyerQuestion.AnswerMaxLength, decision.Draft!.Length);
        Assert.False(decision.SendNow);
    }

    [Theory]
    [InlineData("Serve no Gol?", true)]
    [InlineData("Ainda tem disponível?", false)]
    [InlineData("Qual a versão do motor?", true)]
    [InlineData("É compatível com Uno 2005?", true)]
    [InlineData("Está em bom estado?", false)]
    public void Pergunta_sobre_veiculo(string question, bool expected) => Assert.Equal(expected, BuyerAnswerRules.AsksAboutVehicle(question));
}
