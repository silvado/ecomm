using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ecommerce.Application.Ai;
using Ecommerce.Application.Reports;
using Ecommerce.Domain.Ai;

namespace Ecommerce.Infrastructure.Reports;

/// <summary>
/// Explicação dos problemas escrita pela IA (RF33 CA1): recebe cada problema (o que falhou, quantas vezes e o erro já
/// redigido) e devolve uma frase simples por problema. A ação sugerida não vem da IA; cada texto passa por
/// <see cref="SyncGuardianRules.AcceptExplanation"/>.
/// </summary>
public static class SyncGuardianWriter
{
    internal const string Instructions = """
        Você ajuda o dono de uma loja de autopeças do Brasil a entender falhas na integração da loja com serviços externos
        (Mercado Livre, Instagram/Facebook, gateway de pagamento, frete, NF-e).
        Para cada problema numerado, escreva uma ou duas frases curtas, em português simples, sem termos técnicos,
        explicando o que provavelmente aconteceu a partir do erro. Não diga o que fazer: a ação já vem pronta.
        Não invente números, não cite links, e-mails nem dados de clientes. Se o erro não for claro, diga só que o serviço recusou a operação.
        """;

    internal static readonly JsonObject OutputSchema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["diagnosticos"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["numero"] = new JsonObject { ["type"] = "integer" },
                        ["texto"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("numero", "texto"),
                    ["additionalProperties"] = false,
                },
            },
        },
        ["required"] = new JsonArray("diagnosticos"),
        ["additionalProperties"] = false,
    };

    /// <summary>Resposta da IA simulada (dev e testes): explica o primeiro problema, sem números.</summary>
    internal static readonly JsonObject DevSample = new()
    {
        ["diagnosticos"] = new JsonArray(new JsonObject
        {
            ["numero"] = 1,
            ["texto"] = "O serviço externo recusou a operação por causa de um dado que ele não aceitou.",
        }),
    };

    public static AiRequest Request(IReadOnlyList<SyncProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        var prompt = new StringBuilder();
        for (var i = 0; i < problems.Count; i++)
        {
            var p = problems[i];
            prompt.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(". ").AppendLine(SyncGuardianRules.Title(p))
                .Append("   Vezes: ").AppendLine(p.Occurrences.ToString(CultureInfo.InvariantCulture))
                .Append("   Erro: ").AppendLine(p.LastError ?? "(sem detalhe)");
        }
        var summary = "guardião: " + string.Join("; ", problems.Select(p => p.Key));
        return new AiRequest(AiFeature.SyncDiagnosis, Instructions, prompt.ToString(), [], OutputSchema,
            summary.Length > 1_000 ? summary[..1_000] : summary, MaxOutputTokens: 1024);
    }

    /// <summary>Texto por posição do problema (base 0). Números fora da lista são ignorados.</summary>
    public static IReadOnlyDictionary<int, string> Read(JsonElement output, int count)
    {
        var texts = new Dictionary<int, string>();
        if (output.ValueKind != JsonValueKind.Object || !output.TryGetProperty("diagnosticos", out var list) || list.ValueKind != JsonValueKind.Array)
            return texts;
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("numero", out var n) || n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out var number)
                || !item.TryGetProperty("texto", out var t) || t.ValueKind != JsonValueKind.String)
                continue;
            if (number >= 1 && number <= count && t.GetString() is { } text) texts.TryAdd(number - 1, text);
        }
        return texts;
    }
}
