using Ecommerce.Application.Reports;

namespace Ecommerce.Application.Tests.Reports;

/// <summary>RF33 — o que é problema, que ação sugerir e a conferência da explicação da IA.</summary>
public sealed class SyncGuardianRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 15, 0, 0, TimeSpan.Zero);

    private static IntegrationCall Call(int minutesAgo, bool success, string? error = "HTTP 500", string operation = "atualizar anúncio", bool aboutPart = false) =>
        new("mercado-livre", operation, success, Now.AddMinutes(-minutesAgo), success ? null : error, aboutPart);

    private static SyncProblem Problem(SyncProblemKind kind, string? error, bool aboutPart = false, int occurrences = 3) =>
        new("chave", kind, "mercado-livre", "atualizar anúncio", occurrences, Now, error, aboutPart);

    [Fact]
    public void Tres_falhas_seguidas_da_mesma_chamada_viram_problema()
    {
        var problem = Assert.Single(SyncGuardianRules.RepeatedCallFailures([Call(30, false), Call(20, false), Call(10, false, "HTTP 503")], Now));
        Assert.Equal(("chamada:mercado-livre:atualizar anúncio", 3, "HTTP 503"), (problem.Key, problem.Occurrences, problem.LastError));
        Assert.Equal(Now.AddMinutes(-10), problem.LastAt);
    }

    [Fact]
    public void Chamada_que_voltou_a_dar_certo_nao_e_problema()
    {
        Assert.Empty(SyncGuardianRules.RepeatedCallFailures([Call(30, false), Call(20, false), Call(15, false), Call(5, true)], Now));
    }

    [Fact]
    public void Contam_so_as_falhas_depois_do_ultimo_acerto_e_dentro_da_janela()
    {
        Assert.Empty(SyncGuardianRules.RepeatedCallFailures([Call(50, false), Call(40, true), Call(30, false), Call(20, false)], Now));
        Assert.Empty(SyncGuardianRules.RepeatedCallFailures([Call(200, false), Call(180, false), Call(10, false)], Now));
    }

    [Fact]
    public void Operacoes_diferentes_nao_se_somam()
    {
        Assert.Empty(SyncGuardianRules.RepeatedCallFailures([Call(30, false), Call(20, false, operation: "cotar frete"), Call(10, false)], Now));
    }

    [Fact]
    public void Problema_ja_avisado_nao_entra_de_novo()
    {
        var a = Problem(SyncProblemKind.ChannelReauth, null) with { Key = "acesso:mercado-livre" };
        var b = Problem(SyncProblemKind.BackgroundFailure, null) with { Key = "falha:Atualizar anúncio no canal" };
        Assert.Equal([b], SyncGuardianRules.ToAlert([a, b], ["acesso:mercado-livre"]));
    }

    [Theory]
    [InlineData(SyncProblemKind.ChannelReauth, null, false, SyncAction.Reconnect)]
    [InlineData(SyncProblemKind.RepeatedCallFailure, "HTTP 401: invalid_token", false, SyncAction.Reconnect)]
    [InlineData(SyncProblemKind.RepeatedCallFailure, "HTTP 400: item.attributes.BRAND is required", true, SyncAction.FixPart)]
    [InlineData(SyncProblemKind.BackgroundFailure, "Atributo obrigatório ausente", true, SyncAction.FixPart)]
    [InlineData(SyncProblemKind.BackgroundFailure, "HTTP 503", false, SyncAction.Replay)]
    [InlineData(SyncProblemKind.RepeatedCallFailure, "HTTP 502 Bad Gateway", false, SyncAction.WaitService)]
    [InlineData(SyncProblemKind.RepeatedCallFailure, "The operation has timed out", false, SyncAction.WaitService)]
    [InlineData(SyncProblemKind.RepeatedCallFailure, "HTTP 400: dado inválido", false, SyncAction.ContactSupport)]
    public void Acao_sugerida_vem_das_regras(SyncProblemKind kind, string? error, bool aboutPart, SyncAction expected)
    {
        Assert.Equal(expected, SyncGuardianRules.Classify(Problem(kind, error, aboutPart)));
    }

    [Fact]
    public void Explicacao_simples_da_IA_e_aceita()
    {
        var problem = Problem(SyncProblemKind.RepeatedCallFailure, "HTTP 400: item.attributes.BRAND is required");
        const string text = "O Mercado Livre recusou o anúncio porque falta a marca da peça. Isso aconteceu 3 vezes (erro 400).";
        Assert.Equal(text, SyncGuardianRules.AcceptExplanation(text, problem));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Falhou 7 vezes.")] // número que não está no problema
    [InlineData("Veja em https://exemplo.com o que fazer.")]
    [InlineData("Escreva para suporte@exemplo.com.")]
    public void Explicacao_recusada_volta_para_o_texto_fixo(string text)
    {
        var problem = Problem(SyncProblemKind.RepeatedCallFailure, "HTTP 503");
        Assert.Null(SyncGuardianRules.AcceptExplanation(text, problem));
        Assert.Equal("A chamada falhou 3 vezes seguidas nas últimas 2 horas.", SyncGuardianRules.Diagnose(problem, text).Explanation);
    }

    [Fact]
    public void Explicacao_longa_demais_e_recusada()
    {
        Assert.Null(SyncGuardianRules.AcceptExplanation(new string('a', SyncGuardianRules.MaxExplanationLength + 1), Problem(SyncProblemKind.BackgroundFailure, null)));
    }

    [Fact]
    public void Diagnostico_traz_titulo_e_acao_em_linguagem_simples()
    {
        var d = SyncGuardianRules.Diagnose(Problem(SyncProblemKind.ChannelReauth, "Token revogado") with { Occurrences = 1 });
        Assert.Equal("Mercado Livre: a conexão perdeu a autorização", d.Title);
        Assert.Equal(SyncAction.Reconnect, d.Action);
        Assert.Equal("Abra Canais de venda e conecte a conta de novo.", d.ActionText);
        Assert.StartsWith("A loja precisa autorizar de novo o acesso a Mercado Livre.", d.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Trabalho_em_anuncio_e_reconhecido_pela_descricao()
    {
        Assert.True(SyncGuardianRules.IsListingWork("Atualizar anúncio no canal"));
        Assert.True(SyncGuardianRules.IsListingWork("Atualizar item no catálogo do Instagram/Facebook"));
        Assert.False(SyncGuardianRules.IsListingWork("Confirmar pagamento no gateway"));
    }
}
