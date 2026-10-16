using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ecommerce.Application.Ai;

/// <summary>Imagem enviada à IA (ex.: foto da peça). Tipos aceitos pela API: JPEG, PNG, GIF e WebP.</summary>
public sealed record AiImage(string MediaType, ReadOnlyMemory<byte> Data);

/// <summary>
/// Um pedido à IA (ADR-0003, complemento de 16/10/2026). Cada caso de uso (cadastro por foto, atendimento…) monta o seu.
/// A resposta é sempre um objeto JSON no formato <paramref name="OutputSchema"/>.
/// </summary>
/// <param name="Feature">Uma de <see cref="Domain.Ai.AiFeature"/>: escolhe o modelo, o limite do plano e o registro.</param>
/// <param name="Instructions">Instruções fixas da funcionalidade (prompt de sistema).</param>
/// <param name="Prompt">O pedido em si, só com os dados necessários (sem nome, CPF ou endereço de comprador — LGPD).</param>
/// <param name="OutputSchema">JSON Schema do objeto esperado (todo objeto com <c>additionalProperties: false</c>).</param>
/// <param name="InputSummary">O que vai para o registro no lugar da entrada (RF35 CA1: entrada resumida, sem imagens).</param>
public sealed record AiRequest(
    string Feature, string Instructions, string Prompt, IReadOnlyList<AiImage> Images, JsonObject OutputSchema, string InputSummary,
    int MaxOutputTokens = 2048);

/// <summary>Resposta já conferida: um objeto JSON. Sempre uma sugestão — quem chama decide o que fazer (RF35 CA2).</summary>
public sealed record AiResult(JsonElement Output, string Model, int InputTokens, int OutputTokens);

/// <summary>
/// Porta única para a IA. As implementações só respondem: não recebem repositórios nem serviços que alterem preço,
/// estoque ou pedidos (RF35 CA2, regra 4 do CLAUDE.md). Registro (RF35) e limite do plano (RF36) são decoradores.
/// </summary>
public interface IAiCompletion
{
    /// <exception cref="AiQuotaExceededException">Limite mensal do plano atingido (nada foi chamado).</exception>
    /// <exception cref="AiUnavailableException">Provedor fora do ar ou sem configuração.</exception>
    /// <exception cref="AiInvalidOutputException">Resposta recusada, cortada ou fora do formato: a sugestão é descartada.</exception>
    Task<AiResult> CompleteAsync(AiRequest request, CancellationToken ct = default);
}

public abstract class AiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>RF36 CA2: limite mensal atingido. A mensagem já vai pronta para o usuário.</summary>
public sealed class AiQuotaExceededException(string message) : AiException(message);

/// <summary>Provedor indisponível. <see cref="Transient"/>: vale tentar de novo depois (limite de taxa, sobrecarga, rede).</summary>
public sealed class AiUnavailableException(string message, bool transient, Exception? inner = null) : AiException(message, inner)
{
    public bool Transient { get; } = transient;
}

/// <summary>
/// A IA respondeu, mas a resposta não serve (recusa, cortada, JSON inválido): descartada, nunca aplicada. Leva o consumo
/// para o registro (tokens foram cobrados mesmo assim).
/// </summary>
public sealed class AiInvalidOutputException(string message, string? model = null, int inputTokens = 0, int outputTokens = 0, string? rawOutput = null)
    : AiException(message)
{
    public string? Model { get; } = model;
    public int InputTokens { get; } = inputTokens;
    public int OutputTokens { get; } = outputTokens;
    public string? RawOutput { get; } = rawOutput;
}

/// <summary>Uso de IA do mês na loja do escopo (cartão "Seu plano", RF36 CA1).</summary>
public interface IAiUsage
{
    /// <summary>Usados no mês corrente por funcionalidade.</summary>
    Task<IReadOnlyDictionary<string, int>> CurrentMonthAsync(CancellationToken ct = default);
}
