using System.Text.RegularExpressions;

namespace Ecommerce.Application.Common;

/// <summary>
/// Redação de dados sensíveis em textos que vão para log, log de integração e falhas (RNF05, RF29 CA1): tokens, chaves,
/// segredos, CPF/CNPJ e e-mails viram "***". Nomes de campos sensíveis também são reconhecidos (para logs estruturados).
/// </summary>
public static partial class Redaction
{
    public const string Mask = "***";

    [GeneratedRegex(@"(?i)\b(bearer|basic)\s+[A-Za-z0-9\-._~+/]+=*")]
    private static partial Regex AuthHeader();

    [GeneratedRegex(@"(?i)\b(access_?token|refresh_?token|client_?secret|secret|password|senha|token|api_?key|authorization|webhook_?secret)\b(\s*[""']?\s*[:=]\s*[""']?)[^\s""',;&}]+")]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"\b\d{3}\.?\d{3}\.?\d{3}-?\d{2}\b|\b\d{2}\.?\d{3}\.?\d{3}/?\d{4}-?\d{2}\b")]
    private static partial Regex Document();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    // JWT (cabeçalho base64 de "{" começa com "eyJ") e sequências longas sem espaço com letras e dígitos (tokens, chaves).
    // GUIDs (ids de pedido, mensagem, correlação) não são segredo e ficam: sem eles o log não serve para diagnóstico.
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{5,}\.[A-Za-z0-9_\-]{5,}\.[A-Za-z0-9_\-]{5,}|\b(?=[A-Za-z0-9_\-]*\d)(?=[A-Za-z0-9_\-]*[A-Za-z])[A-Za-z0-9_\-]{33,}\b")]
    private static partial Regex LongToken();

    private static readonly string[] SensitiveNames =
    [
        "password", "senha", "token", "secret", "authorization", "cookie", "cpf", "cnpj", "apikey", "api_key", "accesstoken",
        "refreshtoken", "clientsecret", "webhooksecret", "chave", "renavam", "chassis", "chassi", "plate", "placa",
    ];

    public static bool IsSensitiveName(string name)
    {
        var key = name.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        return SensitiveNames.Any(s => key.Contains(s.Replace("_", "", StringComparison.Ordinal), StringComparison.Ordinal));
    }

    /// <summary>Texto redigido e cortado em <paramref name="max"/> caracteres (nulo continua nulo).</summary>
    public static string? Text(string? value, int max = 500)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var text = AuthHeader().Replace(value, m => $"{m.Groups[1].Value} {Mask}");
        text = KeyValue().Replace(text, m => $"{m.Groups[1].Value}{m.Groups[2].Value}{Mask}");
        text = LongToken().Replace(text, m => Guid.TryParse(m.Value, out _) ? m.Value : Mask);
        text = Document().Replace(text, Mask);
        text = Email().Replace(text, Mask);
        return text.Length > max ? text[..max] : text;
    }
}
