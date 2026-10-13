namespace Ecommerce.Domain.Fiscal;

/// <summary>Como a loja emite a NF-e (RF19, Q37).</summary>
public enum FiscalMode
{
    /// <summary>Sem emissão pela plataforma (padrão).</summary>
    Disabled,
    /// <summary>Pelo provedor fiscal conectado pela loja ("traga sua conta"; primeiro: Focus NFe).</summary>
    Provider,
    /// <summary>A loja emite fora (ERP, emissor próprio) e informa a chave ou o XML no pedido.</summary>
    External,
}

public enum FiscalEnvironment
{
    /// <summary>Testes na SEFAZ: nota sem valor fiscal.</summary>
    Homologation,
    Production,
}

public enum TaxRegime
{
    /// <summary>Simples Nacional: situação tributária do ICMS pelo CSOSN (3 dígitos).</summary>
    SimplesNacional,
    /// <summary>Regime Normal (Lucro Presumido ou Real): CST do ICMS (2 dígitos).</summary>
    Normal,
}

/// <summary>Dados fiscais da peça (RF19 CA3). Nulo = usa o padrão da loja.</summary>
public sealed record PartFiscalData(string? Ncm, string? TaxCode, int? Origin);

/// <summary>
/// Configuração fiscal da loja (RF19 CA3): modo, ambiente, regime e padrões das peças. CFOP é da loja (dentro e fora do
/// estado); a peça só sobrepõe NCM, CSOSN/CST e origem (HIPÓTESE).
/// </summary>
public sealed record FiscalSettings(
    FiscalMode Mode,
    FiscalEnvironment Environment,
    TaxRegime Regime,
    string? State,
    string? DefaultNcm,
    string? CfopInState,
    string? CfopOutOfState,
    string? DefaultTaxCode,
    int? DefaultOrigin)
{
    public static readonly FiscalSettings Default = new(FiscalMode.Disabled, FiscalEnvironment.Homologation, TaxRegime.SimplesNacional,
        null, null, "5102", "6102", "102", 0);
}

/// <summary>Validação dos códigos fiscais (formatos da NF-e 4.00).</summary>
public static class FiscalRules
{
    public static readonly string[] States =
        ["AC", "AL", "AP", "AM", "BA", "CE", "DF", "ES", "GO", "MA", "MT", "MS", "MG", "PA", "PB", "PR", "PE", "PI", "RJ", "RN", "RS", "RO", "RR", "SC", "SP", "SE", "TO"];

    /// <summary>CSOSN do Simples Nacional.</summary>
    public static readonly string[] Csosn = ["101", "102", "103", "201", "202", "203", "300", "400", "500", "900"];

    /// <summary>CST do ICMS no Regime Normal.</summary>
    public static readonly string[] IcmsCst = ["00", "10", "20", "30", "40", "41", "50", "51", "60", "70", "90"];

    /// <summary>Origem da mercadoria: 0 (nacional) a 8.</summary>
    public const int MaxOrigin = 8;

    public static string? NormalizeNcm(string? ncm)
    {
        var digits = Digits(ncm);
        if (digits is null) return null;
        if (digits.Length != 8) throw new ArgumentException("NCM tem 8 dígitos (ex.: 8708.29.99).", nameof(ncm));
        return digits;
    }

    public static string? NormalizeCfop(string? cfop, char firstDigit, string field)
    {
        var digits = Digits(cfop);
        if (digits is null) return null;
        if (digits.Length != 4 || digits[0] != firstDigit)
            throw new ArgumentException($"{field}: CFOP de 4 dígitos começando por {firstDigit} (ex.: {firstDigit}102).", nameof(cfop));
        return digits;
    }

    public static string? NormalizeTaxCode(string? code, TaxRegime regime)
    {
        var digits = Digits(code);
        if (digits is null) return null;
        var valid = regime == TaxRegime.SimplesNacional ? Csosn : IcmsCst;
        if (!valid.Contains(digits))
            throw new ArgumentException(regime == TaxRegime.SimplesNacional
                ? "CSOSN inválido (ex.: 102 — Simples Nacional sem permissão de crédito)."
                : "CST do ICMS inválido (ex.: 00 — tributada integralmente).", nameof(code));
        return digits;
    }

    public static int? NormalizeOrigin(int? origin) =>
        origin is null or (>= 0 and <= MaxOrigin) ? origin : throw new ArgumentException("Origem da mercadoria de 0 a 8 (0 = nacional).", nameof(origin));

    public static FiscalSettings Normalize(FiscalSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Enum.IsDefined(settings.Mode) || !Enum.IsDefined(settings.Environment) || !Enum.IsDefined(settings.Regime))
            throw new ArgumentException("Configuração fiscal inválida.", nameof(settings));
        var state = string.IsNullOrWhiteSpace(settings.State) ? null : settings.State.Trim().ToUpperInvariant();
        if (state is not null && !States.Contains(state)) throw new ArgumentException("UF da loja inválida.", nameof(settings));
        var normalized = new FiscalSettings(settings.Mode, settings.Environment, settings.Regime, state, NormalizeNcm(settings.DefaultNcm),
            NormalizeCfop(settings.CfopInState, '5', "CFOP dentro do estado"), NormalizeCfop(settings.CfopOutOfState, '6', "CFOP fora do estado"),
            NormalizeTaxCode(settings.DefaultTaxCode, settings.Regime), NormalizeOrigin(settings.DefaultOrigin));
        // Emitindo pela plataforma, o mínimo para montar uma nota precisa estar preenchido.
        if (normalized.Mode == FiscalMode.Provider && (normalized.State is null || normalized.CfopInState is null || normalized.CfopOutOfState is null
            || normalized.DefaultTaxCode is null || normalized.DefaultOrigin is null))
            throw new ArgumentException("Para emitir pela plataforma, informe UF da loja, CFOP dentro e fora do estado, CSOSN/CST e origem padrão.", nameof(settings));
        return normalized;
    }

    public static PartFiscalData Normalize(PartFiscalData data, TaxRegime regime)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new PartFiscalData(NormalizeNcm(data.Ncm), NormalizeTaxCode(data.TaxCode, regime), NormalizeOrigin(data.Origin));
    }

    private static string? Digits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.Any(c => !(char.IsAsciiDigit(c) || c is '.' or '-' or ' '))) throw new ArgumentException("Use só números.", nameof(value));
        return new string(text.Where(char.IsAsciiDigit).ToArray());
    }
}

/// <summary>
/// Chave de acesso da NF-e (44 posições). Desde a NT 2025.001 o CNPJ do emitente (posições 7 a 20) pode ter letras;
/// o dígito verificador é o módulo 11 (pesos 2 a 9 da direita para a esquerda) com o valor de cada caractere = código
/// ASCII − 48 (dígitos ficam iguais). Fonte: NT Conjunta 2025.001 (CNPJ alfanumérico nos DF-e), resumida em
/// https://www.machadomeyer.com.br/pt/inteligencia-juridica/publicacoes-ij/tributario-ij/orientacoes-para-implementacao-do-cnpj-alfanumerico-nos-df-e
/// — conferir contra exemplos oficiais ao ligar o adaptador real.
/// </summary>
public static class AccessKey
{
    public const int Length = 44;

    public static string Normalize(string? key)
    {
        var text = new string((key ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c != '.').ToArray()).ToUpperInvariant();
        if (text.Length != Length || !text.Select((c, i) => i is >= 6 and < 20 ? char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c) : char.IsAsciiDigit(c)).All(ok => ok))
            throw new ArgumentException("A chave de acesso tem 44 números (o CNPJ dentro dela pode ter letras).", nameof(key));
        if (CheckDigit(text[..43]) != text[43] - '0') throw new ArgumentException("Chave de acesso inválida: confira os números.", nameof(key));
        if (text.Substring(20, 2) != "55") throw new ArgumentException("A chave não é de NF-e (modelo 55).", nameof(key));
        return text;
    }

    /// <summary>CNPJ do emitente contido na chave.</summary>
    public static string IssuerCnpj(string normalizedKey) => normalizedKey.Substring(6, 14);

    public static int CheckDigit(string first43)
    {
        var sum = 0;
        var weight = 2;
        for (var i = first43.Length - 1; i >= 0; i--)
        {
            sum += (first43[i] - '0') * weight;
            weight = weight == 9 ? 2 : weight + 1;
        }
        var remainder = sum % 11;
        return remainder < 2 ? 0 : 11 - remainder;
    }
}
