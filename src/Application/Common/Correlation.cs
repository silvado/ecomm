namespace Ecommerce.Application.Common;

/// <summary>
/// Contexto de diagnóstico do fluxo atual (RNF06): a correlação liga a requisição, as mensagens que ela gera e as chamadas
/// externas (log de integração, RF29); o tenant vai para todo log. Definido na entrada (HTTP ou mensagem) e lido por
/// quem registra — AsyncLocal, então acompanha o fluxo assíncrono sem passar parâmetro.
/// </summary>
public static class Correlation
{
    public const int MaxLength = 64;

    private static readonly AsyncLocal<string?> CurrentId = new();
    private static readonly AsyncLocal<Guid?> CurrentTenant = new();

    public static string? Current
    {
        get => CurrentId.Value;
        set => CurrentId.Value = Sanitize(value);
    }

    public static Guid? TenantId
    {
        get => CurrentTenant.Value;
        set => CurrentTenant.Value = value;
    }

    /// <summary>Só letras, dígitos, "-", "_" e "."; até <see cref="MaxLength"/> (valor que vem de fora não vira injeção em log).</summary>
    public static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = new string(value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').Take(MaxLength).ToArray());
        return clean.Length == 0 ? null : clean;
    }
}
