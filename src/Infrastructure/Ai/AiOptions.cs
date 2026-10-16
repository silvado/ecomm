namespace Ecommerce.Infrastructure.Ai;

/// <summary>Configuração da IA (ADR-0003): provedor, modelo por funcionalidade e preços. Ids de modelo nunca no código.</summary>
public sealed class AiOptions
{
    public const string Section = "Ai";
    public const string FakeProvider = "simulado";
    public const string AnthropicProvider = "anthropic";

    /// <summary>"simulado" (dev e testes; recusado em produção), "anthropic" ou vazio (IA desligada).</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Modelo das funcionalidades sem modelo próprio em <see cref="Models"/>.</summary>
    public string DefaultModel { get; set; } = string.Empty;

    /// <summary>Funcionalidade → modelo (ex.: um modelo com visão para o cadastro por foto).</summary>
    public Dictionary<string, string> Models { get; set; } = [];

    /// <summary>
    /// Modelo → preço em dólares por milhão de tokens, para o custo estimado do registro (RF35). Preencher com a tabela oficial
    /// de preços da Anthropic; modelo sem preço registra custo zero.
    /// </summary>
    public Dictionary<string, AiModelPrice> Prices { get; set; } = [];

    public AnthropicOptions Anthropic { get; set; } = new();

    public string ModelFor(string feature) =>
        Models.TryGetValue(feature, out var model) && !string.IsNullOrWhiteSpace(model) ? model : DefaultModel;

    public decimal CostOf(string model, int inputTokens, int outputTokens) =>
        Prices.TryGetValue(model, out var price)
            ? (inputTokens * price.InputPerMillion + outputTokens * price.OutputPerMillion) / 1_000_000m
            : 0m;
}

public sealed class AiModelPrice
{
    public decimal InputPerMillion { get; set; }
    public decimal OutputPerMillion { get; set; }
}

public sealed class AnthropicOptions
{
    /// <summary>Chave da plataforma (variável de ambiente <c>Ai__Anthropic__ApiKey</c>; nunca no Git — regra 3).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Vazio = https://api.anthropic.com. Testes apontam para um servidor simulado.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 90;

    /// <summary>Novas tentativas do SDK em 408/409/429/5xx e erros de rede.</summary>
    public int MaxRetries { get; set; } = 2;
}
